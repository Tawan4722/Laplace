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
        var archive = _archiveReader.Read(archivePath);
        ArchiveReader.ValidateEntryBlockReferences(archive);
        var blocksByFileId = ArchiveReader.BuildBlockLookup(archive);
        var encryptionKey = Array.Empty<byte>();
        if (archive.Header.IsEncrypted)
        {
            if (options.Password is null)
            {
                throw new ArchivePasswordRequiredException(archivePath);
            }

            encryptionKey = ArchiveEncryption.DeriveKey(options.Password, archive.Header.EncryptionSalt, archive.Header.KeyDerivationIterations);
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
            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

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
        finally
        {
            if (encryptionKey.Length > 0)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
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
}
