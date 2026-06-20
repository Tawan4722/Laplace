using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Security;
using System.Security.Cryptography;
using Laplace.Core.Exceptions;

namespace Laplace.Core.Services;

public sealed class ArchiveExtractor
{
    private readonly ICompressorRegistry _compressorRegistry;
    private readonly ArchiveReader _archiveReader;

    public ArchiveExtractor(ICompressorRegistry compressorRegistry, ArchiveReader? archiveReader = null)
    {
        _compressorRegistry = compressorRegistry;
        _archiveReader = archiveReader ?? new ArchiveReader();
    }

    public async Task ExtractAsync(
        string archivePath,
        string destinationFolder,
        ExtractArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var archive = _archiveReader.Read(archivePath, options.Password);
        ArchiveReader.ValidateEntryBlockReferences(archive);
        var encryptionKey = Array.Empty<byte>();
        if (archive.Header.IsEncrypted)
        {
            if (options.Password is null)
            {
                throw new ArchivePasswordRequiredException(archivePath);
            }

            encryptionKey = ArchiveEncryption.DeriveKey(options.Password, archive.Header);
        }
        var selectedIds = ExpandSelectedIdsWithDescendants(archive.FileEntries, options.SelectedEntryIds);
        var targets = archive.FileEntries
            .Where(x => selectedIds is null || selectedIds.Contains(x.EntryId))
            .OrderBy(x => x.IsDirectory ? 0 : 1)
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalBytes = targets.Where(x => !x.IsDirectory).Sum(x => x.OriginalSize);
        long processedBytes = 0;

        try
        {
            Directory.CreateDirectory(destinationFolder);
            await using var archiveStream = LpcSfxHelper.OpenArchiveStream(archivePath);

            if (archive.Header.IsSolid)
            {
                await ExtractSolidAsync(archive, archiveStream, destinationFolder, options, selectedIds, totalBytes, encryptionKey, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var blocksByFileId = ArchiveReader.BuildBlockLookup(archive);
                foreach (var entry in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outPath = PathSecurity.EnsureSafeExtractionPath(destinationFolder, entry.RelativePath);

                    if (entry.IsDirectory)
                    {
                        PathSecurity.EnsureNoReparsePointInPath(destinationFolder, outPath);
                        Directory.CreateDirectory(outPath);
                        TryRestoreDirectoryMetadata(outPath, entry);
                        continue;
                    }

                    var parentDir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrWhiteSpace(parentDir))
                    {
                        PathSecurity.EnsureNoReparsePointInPath(destinationFolder, parentDir);
                        Directory.CreateDirectory(parentDir);
                    }

                    if (!options.Overwrite && File.Exists(outPath))
                    {
                        throw new IOException($"File already exists: {outPath}. Use overwrite mode to replace.");
                    }

                    PathSecurity.EnsureNoReparsePointInPath(destinationFolder, outPath);
                    await using var output = new FileStream(
                        outPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1 << 20,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    IncrementalHash? hash = options.VerifyChecksums && entry.ChecksumType == ChecksumType.Sha256
                        ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                        : null;

                    try
                    {
                        if (!blocksByFileId.TryGetValue(entry.EntryId, out var fileBlocks))
                        {
                            fileBlocks = [];
                        }

                        foreach (var block in fileBlocks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var decompressed = await ReadBlockAsync(archive, archiveStream, block, encryptionKey, cancellationToken).ConfigureAwait(false);
                            hash?.AppendData(decompressed);
                            await output.WriteAsync(decompressed, cancellationToken).ConfigureAwait(false);
                            processedBytes += decompressed.Length;
                            progress?.Report(new ArchiveOperationProgress
                            {
                                CurrentItem = entry.RelativePath,
                                ProcessedBytes = processedBytes,
                                TotalBytes = totalBytes,
                                Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                            });
                        }

                        if (hash is not null)
                        {
                            var fileHash = hash.GetHashAndReset();
                            if (!fileHash.SequenceEqual(entry.FileChecksum))
                            {
                                throw new InvalidDataException($"File checksum mismatch for {entry.RelativePath}.");
                            }
                        }
                    }
                    finally
                    {
                        hash?.Dispose();
                    }

                    TryRestoreFileMetadata(outPath, entry);
                }
            }
        }
        finally
        {
            if (encryptionKey.Length > 0)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    private async Task ExtractSolidAsync(
        ArchiveDocument archive,
        Stream archiveStream,
        string destinationFolder,
        ExtractArchiveOptions options,
        IReadOnlySet<long>? selectedIds,
        long totalBytes,
        byte[] encryptionKey,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var directory in archive.FileEntries
                     .Where(x => x.IsDirectory && (selectedIds is null || selectedIds.Contains(x.EntryId)))
                     .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var outPath = PathSecurity.EnsureSafeExtractionPath(destinationFolder, directory.RelativePath);
            PathSecurity.EnsureNoReparsePointInPath(destinationFolder, outPath);
            Directory.CreateDirectory(outPath);
            TryRestoreDirectoryMetadata(outPath, directory);
        }

        var orderedFiles = archive.FileEntries
            .Where(x => !x.IsDirectory)
            .OrderBy(x => x.DataStreamOffset)
            .ThenBy(x => x.EntryId)
            .ToList();
        long processedBytes = 0;
        long logicalOffset = 0;
        var fileIndex = 0;
        var fileOffset = 0L;

        fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex).ConfigureAwait(false);
        SolidExtractionSession? session = null;

        try
        {
            foreach (var block in archive.BlockEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.OriginalStreamOffset != logicalOffset)
                {
                    throw new InvalidDataException($"Unexpected solid block stream offset at block #{block.BlockId}.");
                }

                var decompressed = await ReadBlockAsync(archive, archiveStream, block, encryptionKey, cancellationToken).ConfigureAwait(false);
                var consumed = 0;
                while (consumed < decompressed.Length)
                {
                    if (fileIndex >= orderedFiles.Count)
                    {
                        throw new InvalidDataException("Solid data stream contains more bytes than the file table describes.");
                    }

                    var file = orderedFiles[fileIndex];
                    var remainingInFile = file.OriginalSize - fileOffset;
                    if (remainingInFile <= 0)
                    {
                        await FinalizeSolidSessionAsync(session, file).ConfigureAwait(false);
                        session = null;
                        fileIndex++;
                        fileOffset = 0;
                        fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex).ConfigureAwait(false);
                        continue;
                    }

                    var take = (int)Math.Min(remainingInFile, decompressed.Length - consumed);
                    if (session is null)
                    {
                        session = await OpenSolidSessionAsync(file, destinationFolder, options, selectedIds).ConfigureAwait(false);
                    }

                    if (session is not null)
                    {
                        session.Hash?.AppendData(decompressed, consumed, take);
                        await session.Output.WriteAsync(decompressed.AsMemory(consumed, take), cancellationToken).ConfigureAwait(false);
                        processedBytes += take;
                        progress?.Report(new ArchiveOperationProgress
                        {
                            CurrentItem = file.RelativePath,
                            ProcessedBytes = processedBytes,
                            TotalBytes = totalBytes,
                            Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                        });
                    }

                    logicalOffset += take;
                    fileOffset += take;
                    consumed += take;

                    if (fileOffset == file.OriginalSize)
                    {
                        await FinalizeSolidSessionAsync(session, file).ConfigureAwait(false);
                        session = null;
                        fileIndex++;
                        fileOffset = 0;
                        fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex).ConfigureAwait(false);
                    }
                }
            }

            if (fileIndex != orderedFiles.Count)
            {
                throw new InvalidDataException("Solid archive ended before all file data could be reconstructed.");
            }
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> ReadBlockAsync(
        ArchiveDocument archive,
        Stream archiveStream,
        BlockEntryRecord block,
        byte[] encryptionKey,
        CancellationToken cancellationToken)
    {
        archiveStream.Position = block.DataOffset;
        var compressedBytes = new byte[block.CompressedBlockSize];
        var read = await archiveStream.ReadAsync(compressedBytes.AsMemory(0, compressedBytes.Length), cancellationToken).ConfigureAwait(false);
        if (read != compressedBytes.Length)
        {
            throw new EndOfStreamException($"Unexpected EOF while reading block #{block.BlockId}.");
        }

        var actualChecksum = ChecksumService.ComputeCrc32C(compressedBytes);
        if (actualChecksum != block.BlockChecksumCrc32C)
        {
            throw new InvalidDataException($"Block checksum mismatch at block #{block.BlockId}.");
        }

        if (archive.Header.IsEncrypted)
        {
            try
            {
                compressedBytes = ArchiveEncryption.DecryptBlock(compressedBytes, encryptionKey, block);
            }
            catch (CryptographicException)
            {
                throw new ArchivePasswordException($"Invalid password or corrupted encrypted block #{block.BlockId}.");
            }
        }

        byte[] decompressed;
        if (block.CompressionMethod == CompressionMethod.Raw || block.IsRaw)
        {
            decompressed = compressedBytes;
        }
        else
        {
            var decompressor = _compressorRegistry.GetCompressor(block.CompressionMethod);
            decompressed = decompressor.Decompress(compressedBytes, block.OriginalBlockSize);
        }

        if (decompressed.Length != block.OriginalBlockSize)
        {
            throw new InvalidDataException($"Unexpected decompressed block size at block #{block.BlockId}.");
        }

        return decompressed;
    }

    private static async Task<int> FinalizeEmptySolidFilesAsync(
        IReadOnlyList<FileEntryRecord> orderedFiles,
        string destinationFolder,
        ExtractArchiveOptions options,
        IReadOnlySet<long>? selectedIds,
        int fileIndex)
    {
        while (fileIndex < orderedFiles.Count && orderedFiles[fileIndex].OriginalSize == 0)
        {
            var file = orderedFiles[fileIndex];
            if (selectedIds is null || selectedIds.Contains(file.EntryId))
            {
                var session = await OpenSolidSessionAsync(file, destinationFolder, options, selectedIds).ConfigureAwait(false);
                await FinalizeSolidSessionAsync(session, file).ConfigureAwait(false);
            }

            fileIndex++;
        }

        return fileIndex;
    }

    private static Task<SolidExtractionSession?> OpenSolidSessionAsync(
        FileEntryRecord file,
        string destinationFolder,
        ExtractArchiveOptions options,
        IReadOnlySet<long>? selectedIds)
    {
        if (selectedIds is not null && !selectedIds.Contains(file.EntryId))
        {
            return Task.FromResult<SolidExtractionSession?>(null);
        }

        var outPath = PathSecurity.EnsureSafeExtractionPath(destinationFolder, file.RelativePath);
        var parentDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            PathSecurity.EnsureNoReparsePointInPath(destinationFolder, parentDir);
            Directory.CreateDirectory(parentDir);
        }

        if (!options.Overwrite && File.Exists(outPath))
        {
            throw new IOException($"File already exists: {outPath}. Use overwrite mode to replace.");
        }

        PathSecurity.EnsureNoReparsePointInPath(destinationFolder, outPath);
        var output = new FileStream(
            outPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        IncrementalHash? hash = options.VerifyChecksums && file.ChecksumType == ChecksumType.Sha256
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        return Task.FromResult<SolidExtractionSession?>(new SolidExtractionSession(outPath, output, hash));
    }

    private static async Task FinalizeSolidSessionAsync(SolidExtractionSession? session, FileEntryRecord file)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            if (session.Hash is not null)
            {
                var fileHash = session.Hash.GetHashAndReset();
                if (!fileHash.SequenceEqual(file.FileChecksum))
                {
                    throw new InvalidDataException($"File checksum mismatch for {file.RelativePath}.");
                }
            }
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        TryRestoreFileMetadata(session.Path, file);
    }

    private static void TryRestoreDirectoryMetadata(string path, FileEntryRecord entry)
    {
        try
        {
            Directory.SetCreationTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedUnixMilliseconds).UtcDateTime);
            Directory.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedUnixMilliseconds).UtcDateTime);
            if (entry.FileAttributes != 0)
            {
                File.SetAttributes(path, (FileAttributes)entry.FileAttributes);
            }
        }
        catch
        {
            // metadata restoration failure is non-fatal
        }
    }

    private static void TryRestoreFileMetadata(string path, FileEntryRecord entry)
    {
        try
        {
            File.SetCreationTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedUnixMilliseconds).UtcDateTime);
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedUnixMilliseconds).UtcDateTime);
            if (entry.FileAttributes != 0)
            {
                File.SetAttributes(path, (FileAttributes)entry.FileAttributes);
            }
        }
        catch
        {
            // metadata restoration failure is non-fatal
        }
    }

    private static HashSet<long>? ExpandSelectedIdsWithDescendants(IReadOnlyList<FileEntryRecord> entries, IReadOnlySet<long>? selected)
    {
        if (selected is null)
        {
            return null;
        }

        var byParent = entries
            .GroupBy(e => e.ParentFolderId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.EntryId).ToList());

        var expanded = new HashSet<long>(selected);
        var queue = new Queue<long>(selected);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!byParent.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (expanded.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return expanded;
    }

    private sealed class SolidExtractionSession : IAsyncDisposable
    {
        public SolidExtractionSession(string path, FileStream output, IncrementalHash? hash)
        {
            Path = path;
            Output = output;
            Hash = hash;
        }

        public string Path { get; }
        public FileStream Output { get; }
        public IncrementalHash? Hash { get; }

        public async ValueTask DisposeAsync()
        {
            Hash?.Dispose();
            await Output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
