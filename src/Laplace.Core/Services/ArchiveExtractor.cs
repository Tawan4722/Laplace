using Laplace.Core.Abstractions;
using Laplace.Core.Compression;
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

    public async Task<ExtractResult> ExtractAsync(
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
        var result = new ExtractResult();
        var errorLock = new object();
        int succeededFiles = 0;
        int failedFiles = 0;

        try
        {
            Directory.CreateDirectory(destinationFolder);
            await using var archiveStream = LpcSfxHelper.OpenArchiveStream(archivePath);

            if (archive.Header.IsSolid)
            {
                await ExtractSolidAsync(archive, archiveStream, destinationFolder, options, selectedIds, totalBytes, encryptionKey, result, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var blocksByFileId = ArchiveReader.BuildBlockLookup(archive);
                var threads = options.Threads > 0 ? options.Threads : Environment.ProcessorCount;
                var sem = new SemaphoreSlim(threads);
                var progressLock = new object();

                var tasks = targets.Select(async entry =>
                {
                    await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var outPath = PathSecurity.EnsureSafeExtractionPath(destinationFolder, entry.RelativePath);

                            if (entry.IsDirectory)
                            {
                                PathSecurity.EnsureNoReparsePointInPath(destinationFolder, outPath);
                                Directory.CreateDirectory(outPath);
                                TryRestoreDirectoryMetadata(outPath, entry);
                                return;
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
                            await using var localArchiveStream = LpcSfxHelper.OpenArchiveStream(archivePath);
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
                                    var decompressed = await ReadAndDecompressBlockAsync(localArchiveStream, archive, block, encryptionKey, cancellationToken).ConfigureAwait(false);
                                    hash?.AppendData(decompressed);
                                    await output.WriteAsync(decompressed, cancellationToken).ConfigureAwait(false);
                                    
                                    var currentProcessed = Interlocked.Add(ref processedBytes, decompressed.Length);
                                    lock (progressLock)
                                    {
                                        progress?.Report(new ArchiveOperationProgress
                                        {
                                            CurrentItem = entry.RelativePath,
                                            ProcessedBytes = currentProcessed,
                                            TotalBytes = totalBytes,
                                            Percent = totalBytes == 0 ? 100 : (double)currentProcessed / totalBytes * 100d
                                        });
                                    }
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
                            Interlocked.Increment(ref succeededFiles);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (options.ContinueOnError)
                        {
                            lock (errorLock)
                            {
                                result.Errors.Add(new ExtractFileError(entry.RelativePath, ex.Message));
                            }
                            Interlocked.Increment(ref failedFiles);
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
                result.SucceededFiles = succeededFiles;
                result.FailedFiles = failedFiles;
            }

            return result;
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
        ExtractResult result,
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
        long producerLogicalOffset = 0;
        var fileIndex = 0;
        var fileOffset = 0L;

        fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex, result).ConfigureAwait(false);
        SolidExtractionSession? session = null;
        var sessionFailedForCurrentFile = false;
        var sessionFileErrorMsg = "";

        try
        {
            var threads = options.Threads > 0 ? options.Threads : Environment.ProcessorCount;
            var maxPending = Math.Max(1, threads);
            var pendingBlocks = new Queue<Task<byte[]>>();

            foreach (var block in archive.BlockEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.OriginalStreamOffset != producerLogicalOffset)
                {
                    throw new InvalidDataException($"Unexpected solid block stream offset at block #{block.BlockId}.");
                }
                producerLogicalOffset += block.OriginalBlockSize;

                archiveStream.Position = block.DataOffset;
                var compressedBytes = System.Buffers.ArrayPool<byte>.Shared.Rent(block.CompressedBlockSize);
                try
                {
                    var read = await archiveStream.ReadAsync(compressedBytes.AsMemory(0, block.CompressedBlockSize), cancellationToken).ConfigureAwait(false);
                    if (read != block.CompressedBlockSize)
                    {
                        throw new EndOfStreamException($"Unexpected EOF while reading block #{block.BlockId}.");
                    }

                    var blockRecord = block;
                    var task = Task.Run(() => ProcessCompressedBlock(archive, compressedBytes, block.CompressedBlockSize, blockRecord, encryptionKey), cancellationToken);
                    pendingBlocks.Enqueue(task);
                }
                catch
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(compressedBytes);
                    throw;
                }

                if (pendingBlocks.Count >= maxPending)
                {
                    var decompressed = await pendingBlocks.Dequeue().ConfigureAwait(false);
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
                            if (sessionFailedForCurrentFile)
                            {
                                result.Errors.Add(new ExtractFileError(file.RelativePath, sessionFileErrorMsg));
                                result.FailedFiles++;
                            }
                            else
                            {
                                await FinalizeSolidSessionAsync(session, file, options, result).ConfigureAwait(false);
                            }
                            session = null;
                            sessionFailedForCurrentFile = false;
                            sessionFileErrorMsg = "";
                            fileIndex++;
                            fileOffset = 0;
                            fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex, result).ConfigureAwait(false);
                            continue;
                        }

                        var take = (int)Math.Min(remainingInFile, decompressed.Length - consumed);
                        if (session is null && !sessionFailedForCurrentFile)
                        {
                            try
                            {
                                session = await OpenSolidSessionAsync(file, destinationFolder, options, selectedIds).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (options.ContinueOnError)
                            {
                                sessionFailedForCurrentFile = true;
                                sessionFileErrorMsg = ex.Message;
                            }
                        }

                        if (session is not null && !sessionFailedForCurrentFile)
                        {
                            try
                            {
                                session.Hash?.AppendData(decompressed, consumed, take);
                                await session.Output.WriteAsync(decompressed.AsMemory(consumed, take), cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (options.ContinueOnError)
                            {
                                sessionFailedForCurrentFile = true;
                                sessionFileErrorMsg = ex.Message;
                                try
                                {
                                    await session.DisposeAsync().ConfigureAwait(false);
                                    if (File.Exists(session.Path))
                                    {
                                        File.Delete(session.Path);
                                    }
                                }
                                catch {}
                                session = null;
                            }
                        }

                        processedBytes += take;
                        if (!sessionFailedForCurrentFile && (selectedIds is null || selectedIds.Contains(file.EntryId)))
                        {
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
                            if (sessionFailedForCurrentFile)
                            {
                                result.Errors.Add(new ExtractFileError(file.RelativePath, sessionFileErrorMsg));
                                result.FailedFiles++;
                            }
                            else
                            {
                                await FinalizeSolidSessionAsync(session, file, options, result).ConfigureAwait(false);
                            }
                            session = null;
                            sessionFailedForCurrentFile = false;
                            sessionFileErrorMsg = "";
                            fileIndex++;
                            fileOffset = 0;
                            fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex, result).ConfigureAwait(false);
                        }
                    }
                }
            }

            while (pendingBlocks.Count > 0)
            {
                var decompressed = await pendingBlocks.Dequeue().ConfigureAwait(false);
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
                        if (sessionFailedForCurrentFile)
                        {
                            result.Errors.Add(new ExtractFileError(file.RelativePath, sessionFileErrorMsg));
                            result.FailedFiles++;
                        }
                        else
                        {
                            await FinalizeSolidSessionAsync(session, file, options, result).ConfigureAwait(false);
                        }
                        session = null;
                        sessionFailedForCurrentFile = false;
                        sessionFileErrorMsg = "";
                        fileIndex++;
                        fileOffset = 0;
                        fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex, result).ConfigureAwait(false);
                        continue;
                    }

                    var take = (int)Math.Min(remainingInFile, decompressed.Length - consumed);
                    if (session is null && !sessionFailedForCurrentFile)
                    {
                        try
                        {
                            session = await OpenSolidSessionAsync(file, destinationFolder, options, selectedIds).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (options.ContinueOnError)
                        {
                            sessionFailedForCurrentFile = true;
                            sessionFileErrorMsg = ex.Message;
                        }
                    }

                    if (session is not null && !sessionFailedForCurrentFile)
                    {
                        try
                        {
                            session.Hash?.AppendData(decompressed, consumed, take);
                            await session.Output.WriteAsync(decompressed.AsMemory(consumed, take), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (options.ContinueOnError)
                        {
                            sessionFailedForCurrentFile = true;
                            sessionFileErrorMsg = ex.Message;
                            try
                            {
                                await session.DisposeAsync().ConfigureAwait(false);
                                if (File.Exists(session.Path))
                                {
                                    File.Delete(session.Path);
                                }
                            }
                            catch {}
                            session = null;
                        }
                    }

                    processedBytes += take;
                    if (!sessionFailedForCurrentFile && (selectedIds is null || selectedIds.Contains(file.EntryId)))
                    {
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
                        if (sessionFailedForCurrentFile)
                        {
                            result.Errors.Add(new ExtractFileError(file.RelativePath, sessionFileErrorMsg));
                            result.FailedFiles++;
                        }
                        else
                        {
                            await FinalizeSolidSessionAsync(session, file, options, result).ConfigureAwait(false);
                        }
                        session = null;
                        sessionFailedForCurrentFile = false;
                        sessionFileErrorMsg = "";
                        fileIndex++;
                        fileOffset = 0;
                        fileIndex = await FinalizeEmptySolidFilesAsync(orderedFiles, destinationFolder, options, selectedIds, fileIndex, result).ConfigureAwait(false);
                    }
                }
            }

            if (fileIndex != orderedFiles.Count)
            {
                throw new InvalidDataException("Solid archive ended before all file data could be reconstructed.");
            }
        }
        catch (Exception ex) when (options.ContinueOnError)
        {
            var activePath = fileIndex < orderedFiles.Count ? orderedFiles[fileIndex].RelativePath : "Archive Stream";
            result.Errors.Add(new ExtractFileError(activePath, $"Fatal solid decompression error: {ex.Message}"));
            result.FailedFiles += (orderedFiles.Count - fileIndex);
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    if (File.Exists(session.Path))
                    {
                        File.Delete(session.Path);
                    }
                }
                catch {}
            }
        }
    }

    private byte[] ProcessCompressedBlock(
        ArchiveDocument archive,
        byte[] rentedArray,
        int length,
        BlockEntryRecord block,
        byte[] encryptionKey)
    {
        try
        {
            var actualChecksum = ChecksumService.ComputeCrc32C(rentedArray.AsSpan(0, length));
            if (actualChecksum != block.BlockChecksumCrc32C)
            {
                throw new InvalidDataException($"Block checksum mismatch at block #{block.BlockId}.");
            }

            var payload = rentedArray.AsSpan(0, length);
            byte[]? decrypted = null;
            if (archive.Header.IsEncrypted)
            {
                try
                {
                    decrypted = ArchiveEncryption.DecryptBlock(payload, encryptionKey, block);
                    payload = decrypted;
                }
                catch (CryptographicException)
                {
                    throw new ArchivePasswordException($"Invalid password or corrupted encrypted block #{block.BlockId}.");
                }
            }

            byte[] decompressed;
            if (block.CompressionMethod == CompressionMethod.Raw || block.IsRaw)
            {
                decompressed = payload.ToArray();
            }
            else
            {
                var decompressor = _compressorRegistry.GetCompressor(block.CompressionMethod);
                decompressed = decompressor.Decompress(payload, block.OriginalBlockSize);
            }

            if (decompressed.Length != block.OriginalBlockSize)
            {
                throw new InvalidDataException($"Decompressed block size mismatch at block #{block.BlockId}.");
            }

            if ((block.Flags & 2u) != 0)
            {
                BcjFilter.DecodeX86(decompressed);
            }

            return decompressed;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    public async Task ExtractFileToStreamAsync(
        ArchiveDocument archive,
        Stream archiveStream,
        FileEntryRecord entry,
        Stream destinationStream,
        byte[] encryptionKey,
        CancellationToken cancellationToken = default)
    {
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Cannot extract a directory to a stream.");
        }

        if (archive.Header.IsSolid)
        {
            var fileStart = entry.DataStreamOffset;
            var fileEnd = fileStart + entry.OriginalSize;

            var overlappingBlocks = archive.BlockEntries
                .Where(b => b.OriginalStreamOffset < fileEnd && b.OriginalStreamOffset + b.OriginalBlockSize > fileStart)
                .OrderBy(b => b.BlockId)
                .ToList();

            foreach (var block in overlappingBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decompressed = await ReadAndDecompressBlockAsync(archiveStream, archive, block, encryptionKey, cancellationToken).ConfigureAwait(false);

                var blockStart = block.OriginalStreamOffset;
                var blockEnd = blockStart + block.OriginalBlockSize;

                var overlapStart = Math.Max(fileStart, blockStart);
                var overlapEnd = Math.Min(fileEnd, blockEnd);

                if (overlapStart < overlapEnd)
                {
                    var offsetInBlock = (int)(overlapStart - blockStart);
                    var length = (int)(overlapEnd - overlapStart);
                    await destinationStream.WriteAsync(decompressed.AsMemory(offsetInBlock, length), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            var blocksByFileId = ArchiveReader.BuildBlockLookup(archive);
            if (!blocksByFileId.TryGetValue(entry.EntryId, out var fileBlocks))
            {
                fileBlocks = [];
            }

            foreach (var block in fileBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decompressed = await ReadAndDecompressBlockAsync(archiveStream, archive, block, encryptionKey, cancellationToken).ConfigureAwait(false);
                await destinationStream.WriteAsync(decompressed.AsMemory(0, decompressed.Length), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> ReadAndDecompressBlockAsync(
        Stream archiveStream,
        ArchiveDocument archive,
        BlockEntryRecord block,
        byte[] encryptionKey,
        CancellationToken cancellationToken)
    {
        archiveStream.Position = block.DataOffset;
        var rentedArray = System.Buffers.ArrayPool<byte>.Shared.Rent(block.CompressedBlockSize);
        try
        {
            var read = await archiveStream.ReadAsync(rentedArray.AsMemory(0, block.CompressedBlockSize), cancellationToken).ConfigureAwait(false);
            if (read != block.CompressedBlockSize)
            {
                throw new EndOfStreamException($"Unexpected EOF while reading block #{block.BlockId}.");
            }

            var actualChecksum = ChecksumService.ComputeCrc32C(rentedArray.AsSpan(0, block.CompressedBlockSize));
            if (actualChecksum != block.BlockChecksumCrc32C)
            {
                throw new InvalidDataException($"Block checksum mismatch at block #{block.BlockId}.");
            }

            byte[]? decrypted = null;
            if (archive.Header.IsEncrypted)
            {
                try
                {
                    decrypted = ArchiveEncryption.DecryptBlock(rentedArray.AsSpan(0, block.CompressedBlockSize), encryptionKey, block);
                }
                catch (CryptographicException)
                {
                    throw new ArchivePasswordException($"Invalid password or corrupted encrypted block #{block.BlockId}.");
                }
            }

            byte[] decompressed;
            if (block.CompressionMethod == CompressionMethod.Raw || block.IsRaw)
            {
                decompressed = decrypted is not null
                    ? decrypted
                    : rentedArray.AsSpan(0, block.CompressedBlockSize).ToArray();
            }
            else
            {
                var decompressor = _compressorRegistry.GetCompressor(block.CompressionMethod);
                decompressed = decompressor.Decompress(
                    decrypted is not null ? decrypted : rentedArray.AsSpan(0, block.CompressedBlockSize),
                    block.OriginalBlockSize);
            }

            if (decompressed.Length != block.OriginalBlockSize)
            {
                throw new InvalidDataException($"Unexpected decompressed block size at block #{block.BlockId}.");
            }

            if ((block.Flags & 2u) != 0)
            {
                BcjFilter.DecodeX86(decompressed);
            }

            return decompressed;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    private static async Task<int> FinalizeEmptySolidFilesAsync(
        IReadOnlyList<FileEntryRecord> orderedFiles,
        string destinationFolder,
        ExtractArchiveOptions options,
        IReadOnlySet<long>? selectedIds,
        int fileIndex,
        ExtractResult result)
    {
        while (fileIndex < orderedFiles.Count && orderedFiles[fileIndex].OriginalSize == 0)
        {
            var file = orderedFiles[fileIndex];
            if (selectedIds is null || selectedIds.Contains(file.EntryId))
            {
                SolidExtractionSession? session = null;
                try
                {
                    session = await OpenSolidSessionAsync(file, destinationFolder, options, selectedIds).ConfigureAwait(false);
                    await FinalizeSolidSessionAsync(session, file, options, result).ConfigureAwait(false);
                }
                catch (Exception ex) when (options.ContinueOnError)
                {
                    result.Errors.Add(new ExtractFileError(file.RelativePath, ex.Message));
                    result.FailedFiles++;
                }
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

    private static async Task FinalizeSolidSessionAsync(SolidExtractionSession? session, FileEntryRecord file, ExtractArchiveOptions options, ExtractResult result)
    {
        if (session is null)
        {
            return;
        }

        var checksumFailed = false;
        var exceptionMsg = "";
        try
        {
            if (session.Hash is not null)
            {
                var fileHash = session.Hash.GetHashAndReset();
                if (!fileHash.SequenceEqual(file.FileChecksum))
                {
                    if (options.ContinueOnError)
                    {
                        checksumFailed = true;
                        exceptionMsg = $"File checksum mismatch for {file.RelativePath}.";
                    }
                    else
                    {
                        throw new InvalidDataException($"File checksum mismatch for {file.RelativePath}.");
                    }
                }
            }
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (checksumFailed)
        {
            result.Errors.Add(new ExtractFileError(file.RelativePath, exceptionMsg));
            result.FailedFiles++;
            try
            {
                if (File.Exists(session.Path))
                {
                    File.Delete(session.Path);
                }
            }
            catch {}
            return;
        }

        result.SucceededFiles++;
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
