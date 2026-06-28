using Laplace.Core.Abstractions;
using Laplace.Core.Compression;
using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Security;
using System.Security.Cryptography;

namespace Laplace.Core.Services;

public sealed class ArchiveWriter
{
    private readonly ICompressorRegistry _compressorRegistry;
    private readonly AdaptiveCompressionEngine _adaptiveCompressionEngine;

    public ArchiveWriter(ICompressorRegistry compressorRegistry, AdaptiveCompressionEngine? adaptiveCompressionEngine = null)
    {
        _compressorRegistry = compressorRegistry;
        _adaptiveCompressionEngine = adaptiveCompressionEngine ?? new AdaptiveCompressionEngine();
    }

    public async Task<ArchiveDocument> CreateAsync(
        IEnumerable<string> inputPaths,
        string outputArchivePath,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scanned = ArchivePathScanner.Scan(inputPaths, options.IncludePatterns, options.ExcludePatterns);
        if (scanned.Count == 0)
        {
            throw new InvalidOperationException("No input files or folders were found.");
        }

        var sorted = scanned
            .OrderBy(x => x.RelativePath.Count(c => c == '/'))
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (options.Mode == CompressionMode.Extreme)
        {
            ExtremeCompressionPolicy.Apply(options);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputArchivePath))!);

        var header = new ArchiveHeader
        {
            FormatVersion = 8,
            CreatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CreatorVersion = 0x00010000,
            DefaultBlockSize = (uint)options.BlockSizeBytes,
            Comment = options.Comment,
            OptionalHeaderMetadataJson = options.OptionalHeaderMetadataJson
        };
        var useSolid = ShouldUseSolidMode(options, sorted);
        if (options.BlockSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.BlockSizeBytes), "Block size must be positive.");
        }

        var isSfx = outputArchivePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (options.VolumeSizeBytes is not null)
        {
            if (isSfx)
            {
                throw new NotSupportedException("Multi-volume output is not supported for SFX archives.");
            }
            ArchiveVolumePathHelper.DeleteExistingVolumes(outputArchivePath);
        }

        if (options.Threads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Threads), "Thread count must be at least 1.");
        }

        if (options.EncryptMetadata && options.Password is null)
        {
            throw new InvalidOperationException("Metadata encryption requires a password or keyfile.");
        }

        if (options.RecoveryPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RecoveryPercent), "Recovery percentage must be between 0 and 100.");
        }

        if (options.LockArchive)
        {
            header.ArchiveFlags |= ArchiveHeader.LockedFlag;
        }

        if (useSolid)
        {
            header.ArchiveFlags |= ArchiveHeader.SolidFlag;
        }

        if (options.EncryptMetadata)
        {
            header.ArchiveFlags |= ArchiveHeader.MetadataEncryptionFlag;
        }

        if (options.RecoveryPercent > 0)
        {
            header.ArchiveFlags |= ArchiveHeader.RecoveryRecordFlag;
            header.RecoveryPercent = options.RecoveryPercent;
        }

        var encryptionKey = Array.Empty<byte>();
        if (options.Password is not null)
        {
            header.ArchiveFlags |= ArchiveHeader.EncryptionFlag;
            header.EncryptionAlgorithmId = ArchiveHeader.EncryptionAlgorithmAes256Gcm;
            header.KeyDerivationAlgorithmId = (byte)options.KeyDerivationAlgorithm;
            switch (options.KeyDerivationAlgorithm)
            {
                case KeyDerivationAlgorithm.Pbkdf2Sha256:
                    if (options.KeyDerivationIterations < CreateArchiveOptions.MinimumKeyDerivationIterations ||
                        options.KeyDerivationIterations > CreateArchiveOptions.MaximumKeyDerivationIterations)
                    {
                        throw new InvalidOperationException(
                            $"PBKDF2 iterations must be between {CreateArchiveOptions.MinimumKeyDerivationIterations:N0} and {CreateArchiveOptions.MaximumKeyDerivationIterations:N0}.");
                    }
                    header.KeyDerivationIterations = options.KeyDerivationIterations;
                    break;
                case KeyDerivationAlgorithm.Argon2id:
                    if (options.Argon2Iterations < CreateArchiveOptions.MinimumArgon2Iterations ||
                        options.Argon2Iterations > CreateArchiveOptions.MaximumArgon2Iterations ||
                        options.Argon2MemoryKiB < CreateArchiveOptions.MinimumArgon2MemoryKiB ||
                        options.Argon2MemoryKiB > CreateArchiveOptions.MaximumArgon2MemoryKiB ||
                        options.Argon2Parallelism < 1 ||
                        options.Argon2Parallelism > CreateArchiveOptions.MaximumArgon2Parallelism)
                    {
                        throw new InvalidOperationException("Argon2id settings are outside the supported safety bounds.");
                    }
                    header.KeyDerivationIterations = options.Argon2Iterations;
                    header.KeyDerivationMemoryKiB = options.Argon2MemoryKiB;
                    header.KeyDerivationParallelism = options.Argon2Parallelism;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported key derivation algorithm: {options.KeyDerivationAlgorithm}.");
            }

            header.EncryptionSalt = ArchiveEncryption.CreateSalt();
            encryptionKey = ArchiveEncryption.DeriveKey(options.Password, header);
        }

        try
        {
            var fileEntries = new List<FileEntryRecord>(sorted.Count);
            var blockEntries = new List<BlockEntryRecord>();
            var directoryIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var totalBytes = sorted.Where(x => !x.IsDirectory).Sum(x => new FileInfo(x.FullPath).Length);
            options.TotalInputSizeBytes = totalBytes;

            isSfx = outputArchivePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var targetPath = isSfx
                ? Path.Combine(Path.GetTempPath(), $"laplace_sfx_{Guid.NewGuid():N}.tmp")
                : outputArchivePath;

            await using Stream archiveStream = options.VolumeSizeBytes is { } volumeSize
                ? new MultiVolumeStream(targetPath, volumeSize)
                : new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1 << 20,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            header.DataSectionOffset = ArchiveFormatCodec.WriteHeader(archiveStream, header);

            if (useSolid)
            {
                await WriteSolidEntriesAsync(sorted, options, header, encryptionKey, archiveStream, fileEntries, blockEntries, directoryIds, totalBytes, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteIndependentEntriesAsync(sorted, options, header, encryptionKey, archiveStream, fileEntries, blockEntries, directoryIds, totalBytes, progress, cancellationToken).ConfigureAwait(false);
            }

            header.FileEntryCount = fileEntries.Count;
            header.BlockEntryCount = blockEntries.Count;
            header.FileTableOffset = archiveStream.Position;
            if (header.IsMetadataEncrypted)
            {
                var plaintext = ArchiveFormatCodec.SerializeFileEntries(fileEntries, header.FormatVersion);
                try
                {
                    ArchiveFormatCodec.WriteEncryptedTable(
                        archiveStream,
                        ArchiveEncryption.EncryptMetadata(plaintext, encryptionKey, "file table", header));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            else
            {
                ArchiveFormatCodec.WriteFileEntries(archiveStream, fileEntries, header.FormatVersion);
            }
            header.BlockTableOffset = archiveStream.Position;
            if (header.IsMetadataEncrypted)
            {
                var plaintext = ArchiveFormatCodec.SerializeBlockEntries(blockEntries, header.FormatVersion);
                try
                {
                    ArchiveFormatCodec.WriteEncryptedTable(
                        archiveStream,
                        ArchiveEncryption.EncryptMetadata(plaintext, encryptionKey, "block table", header));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            else
            {
                ArchiveFormatCodec.WriteBlockEntries(archiveStream, blockEntries, header.FormatVersion);
            }

            if (header.HasRecoveryRecord)
            {
                header.RecoveryRecordOffset = archiveStream.Position;
                header.RecoveryRecordLength = LpcRecoveryService.CalculateRecordLength(
                    header.RecoveryRecordOffset,
                    options.RecoveryPercent);
            }

            archiveStream.Position = 0;
            ArchiveFormatCodec.WriteHeader(archiveStream, header);
            if (header.HasRecoveryRecord)
            {
                archiveStream.Position = header.RecoveryRecordOffset;
                await LpcRecoveryService.WriteRecordAsync(
                    archiveStream,
                    header.RecoveryRecordOffset,
                    options.RecoveryPercent,
                    cancellationToken).ConfigureAwait(false);
            }
            await archiveStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await archiveStream.DisposeAsync().ConfigureAwait(false);

            if (isSfx)
            {
                var stubPath = LpcSfxHelper.GetSfxStubPath();
                if (!File.Exists(stubPath))
                {
                    throw new FileNotFoundException($"Laplace GUI executable stub not found at '{stubPath}'. SFX archive cannot be created.");
                }

                using (var outputStream = new FileStream(outputArchivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var stubStream = new FileStream(stubPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await stubStream.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
                    }

                    long stubLength = outputStream.Position;

                    using (var tempStream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await tempStream.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
                    }

                    var offsetBytes = BitConverter.GetBytes(stubLength);
                    var signatureBytes = System.Text.Encoding.ASCII.GetBytes(LpcSfxHelper.SfxSignature);

                    await outputStream.WriteAsync(offsetBytes, cancellationToken).ConfigureAwait(false);
                    await outputStream.WriteAsync(signatureBytes, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    File.Delete(targetPath);
                }
                catch
                {
                }
            }

            return new ArchiveDocument
            {
                Header = header,
                FileEntries = fileEntries,
                BlockEntries = blockEntries
            };
        }
        finally
        {
            if (encryptionKey.Length > 0)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    private async Task WriteIndependentEntriesAsync(
        IReadOnlyList<InputEntry> sorted,
        CreateArchiveOptions options,
        ArchiveHeader header,
        byte[] encryptionKey,
        Stream archiveStream,
        List<FileEntryRecord> fileEntries,
        List<BlockEntryRecord> blockEntries,
        Dictionary<string, long> directoryIds,
        long totalBytes,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long processedBytes = 0;
        var pendingBlocks = new Queue<Task<PreparedIndependentBlock>>();
        var maxPendingBlocks = Math.Max(1, options.Threads);
        var fileMethods = new Dictionary<long, HashSet<CompressionMethod>>();
        var writtenBlocks = new Dictionary<string, BlockEntryRecord>();

        async Task ProcessCompletedBlockAsync(PreparedIndependentBlock prepared)
        {
            var fileEntry = prepared.FileEntry;
            BlockEntryRecord block;

            if (options.Deduplicate && prepared.OriginalSha256 is not null && writtenBlocks.TryGetValue(prepared.OriginalSha256, out var existingBlock))
            {
                block = new BlockEntryRecord
                {
                    BlockId = blockEntries.Count,
                    OwningFileEntryId = fileEntry.EntryId,
                    OriginalBlockSize = prepared.OriginalBlockSize,
                    CompressedBlockSize = existingBlock.CompressedBlockSize,
                    CompressionMethod = existingBlock.CompressionMethod,
                    CompressionLevel = existingBlock.CompressionLevel,
                    Flags = existingBlock.Flags,
                    IsRaw = existingBlock.IsRaw,
                    DataOffset = existingBlock.DataOffset,
                    BlockChecksumCrc32C = existingBlock.BlockChecksumCrc32C,
                    EncryptionNonce = existingBlock.EncryptionNonce,
                    EncryptionTag = existingBlock.EncryptionTag
                };
                blockEntries.Add(block);

                if (fileEntry.FirstBlockIndex < 0)
                {
                    fileEntry.FirstBlockIndex = block.BlockId;
                }
                fileEntry.CompressedSize += block.CompressedBlockSize;
                fileEntry.OriginalSize += prepared.OriginalBlockSize;
                fileEntry.BlockCount++;
            }
            else
            {
                var compressor = _compressorRegistry.GetCompressor(prepared.Method);
                block = new BlockEntryRecord
                {
                    BlockId = blockEntries.Count,
                    OwningFileEntryId = fileEntry.EntryId,
                    OriginalBlockSize = prepared.OriginalBlockSize,
                    CompressedBlockSize = prepared.CompressedBytes.Length,
                    CompressionMethod = prepared.Method,
                    CompressionLevel = compressor.Level,
                    Flags = (prepared.IsRaw ? 1u : 0u) | (prepared.IsBcj ? 2u : 0u),
                    IsRaw = prepared.IsRaw
                };

                var outputBytes = prepared.CompressedBytes;
                if (header.IsEncrypted)
                {
                    outputBytes = ArchiveEncryption.EncryptBlock(outputBytes, encryptionKey, block);
                }

                block.DataOffset = archiveStream.Position;
                block.BlockChecksumCrc32C = ChecksumService.ComputeCrc32C(outputBytes);
                await archiveStream.WriteAsync(outputBytes, cancellationToken).ConfigureAwait(false);
                blockEntries.Add(block);

                if (options.Deduplicate && prepared.OriginalSha256 is not null)
                {
                    writtenBlocks[prepared.OriginalSha256] = block;
                }

                if (fileEntry.FirstBlockIndex < 0)
                {
                    fileEntry.FirstBlockIndex = block.BlockId;
                }
                fileEntry.CompressedSize += outputBytes.Length;
                fileEntry.OriginalSize += prepared.OriginalBlockSize;
                fileEntry.BlockCount++;
            }

            if (!fileMethods.TryGetValue(fileEntry.EntryId, out var methods))
            {
                methods = [];
                fileMethods[fileEntry.EntryId] = methods;
            }
            methods.Add(block.CompressionMethod);
        }

        foreach (var sourceEntry in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entryId = fileEntries.Count;
            var parentRelative = GetParentRelative(sourceEntry.RelativePath);
            var parentFolderId = string.IsNullOrWhiteSpace(parentRelative) ? -1 : directoryIds[parentRelative];
            var fileEntry = BuildFileEntrySkeleton(sourceEntry, entryId, parentFolderId);

            if (sourceEntry.IsDirectory)
            {
                directoryIds[sourceEntry.RelativePath] = entryId;
                fileEntries.Add(fileEntry);
                continue;
            }

            fileEntries.Add(fileEntry);

            using var fs = new FileStream(
                sourceEntry.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                options.BlockSizeBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(options.BlockSizeBytes);
            try
            {
                CdcChunkReader? cdcReader = options.UseCdc
                    ? new CdcChunkReader(fs, options.MinChunkSize, options.AvgChunkSize, options.MaxChunkSize)
                    : null;

                byte[]? rentedBlockData = null;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[]? blockData;
                    int bytesRead = 0;

                    if (cdcReader is not null)
                    {
                        blockData = await cdcReader.NextChunkAsync(cancellationToken).ConfigureAwait(false);
                        if (blockData is null)
                        {
                            break;
                        }
                        bytesRead = blockData.Length;
                        rentedBlockData = null;
                    }
                    else
                    {
                        var read = await fs.ReadAsync(buffer.AsMemory(0, options.BlockSizeBytes), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }
                        bytesRead = read;
                        rentedBlockData = System.Buffers.ArrayPool<byte>.Shared.Rent(bytesRead);
                        buffer.AsSpan(0, bytesRead).CopyTo(rentedBlockData);
                        blockData = rentedBlockData;
                    }

                    incrementalHash.AppendData(blockData.AsSpan(0, bytesRead));

                    var relativePath = sourceEntry.RelativePath;
                    string? blockHash = null;
                    if (options.Deduplicate)
                    {
                        blockHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blockData.AsSpan(0, bytesRead)));
                    }

                    if (options.Deduplicate && blockHash is not null && writtenBlocks.TryGetValue(blockHash, out var existingBlock))
                    {
                        var prepared = new PreparedIndependentBlock(fileEntry, bytesRead, CompressionMethod.Raw, [], true)
                        {
                            OriginalSha256 = blockHash,
                            IsDuplicate = true,
                            DuplicateOfHash = blockHash
                        };
                        await ProcessCompletedBlockAsync(prepared).ConfigureAwait(false);
                        if (rentedBlockData is not null)
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(rentedBlockData);
                        }
                    }
                    else
                    {
                        if (maxPendingBlocks == 1)
                        {
                            try
                            {
                                var (outputMethod, outputBytes, isRaw, isBcj) = SelectAndCompressBlock(relativePath, options, blockData, bytesRead);
                                var prepared = new PreparedIndependentBlock(fileEntry, bytesRead, outputMethod, outputBytes, isRaw)
                                {
                                    OriginalSha256 = blockHash,
                                    IsBcj = isBcj
                                };
                                await ProcessCompletedBlockAsync(prepared).ConfigureAwait(false);
                            }
                            finally
                            {
                                if (rentedBlockData is not null)
                                {
                                    System.Buffers.ArrayPool<byte>.Shared.Return(rentedBlockData);
                                }
                            }

                            if (options.Mode == CompressionMode.Extreme)
                            {
                                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                            }
                        }
                        else
                        {
                            var currentHash = blockHash;
                            var dataToCompress = blockData;
                            var rentedToReturn = rentedBlockData;
                            var lengthToCompress = bytesRead;
                            pendingBlocks.Enqueue(Task.Run(() =>
                            {
                                try
                                {
                                    var (outputMethod, outputBytes, isRaw, isBcj) = SelectAndCompressBlock(relativePath, options, dataToCompress, lengthToCompress);
                                    return new PreparedIndependentBlock(fileEntry, lengthToCompress, outputMethod, outputBytes, isRaw)
                                    {
                                        OriginalSha256 = currentHash,
                                        IsBcj = isBcj
                                    };
                                }
                                finally
                                {
                                    if (rentedToReturn is not null)
                                    {
                                        System.Buffers.ArrayPool<byte>.Shared.Return(rentedToReturn);
                                    }
                                }
                            }, cancellationToken));

                            if (pendingBlocks.Count >= maxPendingBlocks)
                            {
                                var prepared = await pendingBlocks.Dequeue().ConfigureAwait(false);
                                await ProcessCompletedBlockAsync(prepared).ConfigureAwait(false);
                            }
                        }
                    }

                    processedBytes += bytesRead;

                    progress?.Report(new ArchiveOperationProgress
                    {
                        CurrentItem = sourceEntry.RelativePath,
                        ProcessedBytes = processedBytes,
                        TotalBytes = totalBytes,
                        Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                    });
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }

            fileEntry.FileChecksum = incrementalHash.GetHashAndReset();
        }

        while (pendingBlocks.Count > 0)
        {
            var prepared = await pendingBlocks.Dequeue().ConfigureAwait(false);
            await ProcessCompletedBlockAsync(prepared).ConfigureAwait(false);
        }

        foreach (var fileEntry in fileEntries.Where(x => !x.IsDirectory))
        {
            if (fileMethods.TryGetValue(fileEntry.EntryId, out var methods) && methods.Count > 0)
            {
                fileEntry.CompressionSummary = string.Join(",", methods.OrderBy(x => (int)x));
            }
            else
            {
                fileEntry.CompressionSummary = string.Empty;
            }
        }
    }

    private async Task WriteSolidEntriesAsync(
        IReadOnlyList<InputEntry> sorted,
        CreateArchiveOptions options,
        ArchiveHeader header,
        byte[] encryptionKey,
        Stream archiveStream,
        List<FileEntryRecord> fileEntries,
        List<BlockEntryRecord> blockEntries,
        Dictionary<string, long> directoryIds,
        long totalBytes,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long processedBytes = 0;
        long solidStreamOffset = 0;
        long blockStreamOffset = 0;
        var solidBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(options.BlockSizeBytes);
        var solidBufferLength = 0;
        var blockHintPath = string.Empty;
        var readBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(options.BlockSizeBytes, 256 * 1024));
        try
        {
        var pendingBlocks = new Queue<Task<PreparedSolidBlock>>();
        var maxPendingBlocks = Math.Max(1, options.Threads);

        foreach (var sourceEntry in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entryId = fileEntries.Count;
            var parentRelative = GetParentRelative(sourceEntry.RelativePath);
            var parentFolderId = string.IsNullOrWhiteSpace(parentRelative) ? -1 : directoryIds[parentRelative];
            var fileEntry = BuildFileEntrySkeleton(sourceEntry, entryId, parentFolderId);

            if (sourceEntry.IsDirectory)
            {
                directoryIds[sourceEntry.RelativePath] = entryId;
                fileEntries.Add(fileEntry);
                continue;
            }

            fileEntry.DataStreamOffset = solidStreamOffset;
            using var fs = new FileStream(
                sourceEntry.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                readBuffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = await fs.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                incrementalHash.AppendData(readBuffer, 0, bytesRead);
                var consumed = 0;
                while (consumed < bytesRead)
                {
                    var toCopy = Math.Min(options.BlockSizeBytes - solidBufferLength, bytesRead - consumed);
                    readBuffer.AsSpan(consumed, toCopy).CopyTo(solidBuffer.AsSpan(solidBufferLength, toCopy));
                    blockHintPath = sourceEntry.RelativePath;
                    solidBufferLength += toCopy;
                    consumed += toCopy;
                    solidStreamOffset += toCopy;
                    fileEntry.OriginalSize += toCopy;
                    processedBytes += toCopy;

                    if (solidBufferLength == options.BlockSizeBytes)
                    {
                        var blockId = blockEntries.Count + pendingBlocks.Count;
                        var hintPath = blockHintPath;
                        var streamOffset = blockStreamOffset;
                        if (maxPendingBlocks == 1)
                        {
                            await WritePreparedSolidBlockAsync(
                                PrepareSolidBlock(blockId, hintPath, options, solidBuffer, solidBufferLength, streamOffset),
                                header,
                                encryptionKey,
                                archiveStream,
                                blockEntries,
                                cancellationToken).ConfigureAwait(false);
                            if (options.Mode == CompressionMode.Extreme)
                            {
                                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                            }
                        }
                        else
                        {
                            var blockData = System.Buffers.ArrayPool<byte>.Shared.Rent(solidBufferLength);
                            solidBuffer.AsSpan(0, solidBufferLength).CopyTo(blockData);
                            var lengthToCompress = solidBufferLength;
                            var rentedToReturn = blockData;
                            pendingBlocks.Enqueue(Task.Run(
                                () => {
                                    try {
                                        return PrepareSolidBlock(blockId, hintPath, options, blockData, lengthToCompress, streamOffset);
                                    } finally {
                                        System.Buffers.ArrayPool<byte>.Shared.Return(rentedToReturn);
                                    }
                                },
                                cancellationToken));
                            if (pendingBlocks.Count >= maxPendingBlocks)
                            {
                                await WritePreparedSolidBlockAsync(
                                    await pendingBlocks.Dequeue().ConfigureAwait(false),
                                    header,
                                    encryptionKey,
                                    archiveStream,
                                    blockEntries,
                                    cancellationToken).ConfigureAwait(false);
                            }
                        }
                        blockStreamOffset += solidBufferLength;
                        solidBufferLength = 0;
                    }
                }

                progress?.Report(new ArchiveOperationProgress
                {
                    CurrentItem = sourceEntry.RelativePath,
                    ProcessedBytes = processedBytes,
                    TotalBytes = totalBytes,
                    Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                });
            }

            fileEntry.FileChecksum = incrementalHash.GetHashAndReset();
            fileEntries.Add(fileEntry);
        }

        if (solidBufferLength > 0)
        {
            var blockId = blockEntries.Count + pendingBlocks.Count;
            var hintPath = blockHintPath;
            var streamOffset = blockStreamOffset;
            if (maxPendingBlocks == 1)
            {
                await WritePreparedSolidBlockAsync(
                    PrepareSolidBlock(blockId, hintPath, options, solidBuffer, solidBufferLength, streamOffset),
                    header,
                    encryptionKey,
                    archiveStream,
                    blockEntries,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var blockData = System.Buffers.ArrayPool<byte>.Shared.Rent(solidBufferLength);
                solidBuffer.AsSpan(0, solidBufferLength).CopyTo(blockData);
                var lengthToCompress = solidBufferLength;
                var rentedToReturn = blockData;
                pendingBlocks.Enqueue(Task.Run(
                    () => {
                        try {
                            return PrepareSolidBlock(blockId, hintPath, options, blockData, lengthToCompress, streamOffset);
                        } finally {
                            System.Buffers.ArrayPool<byte>.Shared.Return(rentedToReturn);
                        }
                    },
                    cancellationToken));
            }
        }

        while (pendingBlocks.Count > 0)
        {
            await WritePreparedSolidBlockAsync(
                await pendingBlocks.Dequeue().ConfigureAwait(false),
                header,
                encryptionKey,
                archiveStream,
                blockEntries,
                cancellationToken).ConfigureAwait(false);
        }

        FinalizeSolidFileEntries(fileEntries, blockEntries);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(solidBuffer);
            System.Buffers.ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private PreparedSolidBlock PrepareSolidBlock(
        long blockId,
        string blockHintPath,
        CreateArchiveOptions options,
        byte[] blockData,
        int blockLength,
        long originalStreamOffset)
    {
        var (outputMethod, outputBytes, isRaw, isBcj) = SelectAndCompressBlock(blockHintPath, options, blockData, blockLength);
        var compressor = GetCompressorForCompression(outputMethod, options);
        var flags = (isRaw ? 1u : 0u) | (isBcj ? 2u : 0u);
        return new PreparedSolidBlock(
            new BlockEntryRecord
            {
                BlockId = blockId,
                OwningFileEntryId = -1,
                OriginalBlockSize = blockLength,
                CompressedBlockSize = outputBytes.Length,
                CompressionMethod = outputMethod,
                CompressionLevel = compressor.Level,
                OriginalStreamOffset = originalStreamOffset,
                Flags = flags,
                IsRaw = isRaw
            },
            outputBytes);
    }

    private static async Task WritePreparedSolidBlockAsync(
        PreparedSolidBlock prepared,
        ArchiveHeader header,
        byte[] encryptionKey,
        Stream archiveStream,
        List<BlockEntryRecord> blockEntries,
        CancellationToken cancellationToken)
    {
        var block = prepared.Block;
        var outputBytes = prepared.Bytes;

        if (header.IsEncrypted)
        {
            outputBytes = ArchiveEncryption.EncryptBlock(outputBytes, encryptionKey, block);
        }

        block.DataOffset = archiveStream.Position;
        block.BlockChecksumCrc32C = ChecksumService.ComputeCrc32C(outputBytes);
        await archiveStream.WriteAsync(outputBytes, cancellationToken).ConfigureAwait(false);
        blockEntries.Add(block);
    }

    private static void FinalizeSolidFileEntries(
        List<FileEntryRecord> fileEntries,
        IReadOnlyList<BlockEntryRecord> blockEntries)
    {
        var solidFiles = fileEntries.Where(x => !x.IsDirectory).OrderBy(x => x.DataStreamOffset).ThenBy(x => x.EntryId).ToList();
        foreach (var fileEntry in solidFiles)
        {
            if (fileEntry.OriginalSize == 0)
            {
                fileEntry.FirstBlockIndex = -1;
                fileEntry.BlockCount = 0;
                fileEntry.CompressedSize = 0;
                fileEntry.CompressionSummary = string.Empty;
                continue;
            }

            var fileStart = fileEntry.DataStreamOffset;
            var fileEnd = fileStart + fileEntry.OriginalSize;
            var firstBlockIndex = -1L;
            var blockCount = 0;
            var compressedShare = 0L;
            var methodsUsed = new HashSet<CompressionMethod>();

            foreach (var block in blockEntries)
            {
                var blockStart = block.OriginalStreamOffset;
                var blockEnd = blockStart + block.OriginalBlockSize;
                var overlapStart = Math.Max(fileStart, blockStart);
                var overlapEnd = Math.Min(fileEnd, blockEnd);
                if (overlapEnd <= overlapStart)
                {
                    continue;
                }

                if (firstBlockIndex < 0)
                {
                    firstBlockIndex = block.BlockId;
                }

                blockCount++;
                methodsUsed.Add(block.CompressionMethod);
                var overlapLength = overlapEnd - overlapStart;
                compressedShare += (long)Math.Ceiling((double)block.CompressedBlockSize * overlapLength / block.OriginalBlockSize);
            }

            fileEntry.FirstBlockIndex = firstBlockIndex;
            fileEntry.BlockCount = blockCount;
            fileEntry.CompressedSize = compressedShare;
            fileEntry.CompressionSummary = string.Join(",", methodsUsed.OrderBy(x => (int)x));
        }
    }

    private static FileEntryRecord BuildFileEntrySkeleton(InputEntry sourceEntry, int entryId, long parentFolderId)
    {
        if (sourceEntry.IsDirectory)
        {
            var info = new DirectoryInfo(sourceEntry.FullPath);
            var created = info.Exists ? new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds() : 0;
            var modified = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() : 0;
            return new FileEntryRecord
            {
                EntryId = entryId,
                ParentFolderId = parentFolderId,
                RelativePath = PathSecurity.NormalizeArchivePath(sourceEntry.RelativePath),
                CreatedUnixMilliseconds = created,
                ModifiedUnixMilliseconds = modified,
                IsDirectory = true,
                FileAttributes = (int)FileAttributes.Directory,
                ChecksumType = ChecksumType.None
            };
        }

        var fileInfo = new FileInfo(sourceEntry.FullPath);
        return new FileEntryRecord
        {
            EntryId = entryId,
            ParentFolderId = parentFolderId,
            RelativePath = PathSecurity.NormalizeArchivePath(sourceEntry.RelativePath),
            CreatedUnixMilliseconds = new DateTimeOffset(fileInfo.CreationTimeUtc).ToUnixTimeMilliseconds(),
            ModifiedUnixMilliseconds = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            IsDirectory = false,
            FileAttributes = (int)fileInfo.Attributes,
            ChecksumType = ChecksumType.Sha256
        };
    }

    private static bool ShouldUseSolidMode(CreateArchiveOptions options, IReadOnlyList<InputEntry> sorted)
    {
        var fileCount = sorted.Count(x => !x.IsDirectory);
        return options.SolidMode switch
        {
            SolidMode.On => fileCount > 1,
            SolidMode.Off => false,
            _ => fileCount > 1 && options.Mode is CompressionMode.Balanced or CompressionMode.Maximum or CompressionMode.Intensive or CompressionMode.Compressed or CompressionMode.Extreme
        };
    }

    private byte[] CompressBlock(CompressionMethod preferredMethod, ReadOnlySpan<byte> blockData, CreateArchiveOptions options)
    {
        var compressor = GetCompressorForCompression(preferredMethod, options);
        return compressor.Compress(blockData);
    }

    private static bool IsExecutable(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".sys", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".so", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".dylib", StringComparison.OrdinalIgnoreCase);
    }

    private (CompressionMethod Method, byte[] Bytes, bool IsRaw, bool IsBcj) SelectAndCompressBlock(
        string relativePath,
        CreateArchiveOptions options,
        byte[] blockBuffer,
        int blockLength)
    {
        var blockData = blockBuffer.AsSpan(0, blockLength);
        if (blockData.Length == 0)
        {
            return (CompressionMethod.Raw, [], true, false);
        }

        bool applyBcj = IsExecutable(relativePath);
        byte[]? rentedBcj = null;
        byte[] processedBuffer = blockBuffer;
        if (applyBcj)
        {
            rentedBcj = System.Buffers.ArrayPool<byte>.Shared.Rent(blockLength);
            blockData.CopyTo(rentedBcj);
            BcjFilter.EncodeX86(rentedBcj.AsSpan(0, blockLength));
            processedBuffer = rentedBcj;
        }

        try
        {
            var processedData = processedBuffer.AsSpan(0, blockLength);

            ReadOnlySpan<byte> sample = options.Mode == CompressionMode.Extreme
                ? BuildExtremeSample(processedData)
                : processedData[..Math.Min(processedData.Length, 64 * 1024)];
            var analysis = _adaptiveCompressionEngine.Analyze(relativePath, sample);
            var bestMethod = SelectPreferredMethod(options, analysis, sample);
            if (bestMethod == CompressionMethod.LzmaMax &&
                options.Mode == CompressionMode.Extreme &&
                (blockLength > 32 * 1024 * 1024 ||
                 options.LzmaDictionarySizeBytes is > 8 * 1024 * 1024))
            {
                bestMethod = CompressionMethod.ZstdHigh;
            }
            if (bestMethod == CompressionMethod.Raw &&
                options.Mode == CompressionMode.Extreme &&
                options.ZstdForceLongDistanceTrial)
            {
                bestMethod = CompressionMethod.ZstdHigh;
            }

            if (bestMethod == CompressionMethod.Raw)
            {
                return (CompressionMethod.Raw, GetRawBlockBytes(blockBuffer, blockLength), true, false);
            }

            byte[] compressed;
            try
            {
                compressed = CompressBlock(bestMethod, processedData, options);
            }
            catch
            {
                return (CompressionMethod.Raw, GetRawBlockBytes(blockBuffer, blockLength), true, false);
            }

            if (compressed.Length >= blockData.Length)
            {
                return (CompressionMethod.Raw, GetRawBlockBytes(blockBuffer, blockLength), true, false);
            }

            return (bestMethod, compressed, false, applyBcj);
        }
        finally
        {
            if (rentedBcj is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBcj);
            }
        }
    }

    private static byte[] GetRawBlockBytes(byte[] blockBuffer, int blockLength)
    {
        return blockLength == blockBuffer.Length
            ? blockBuffer
            : blockBuffer.AsSpan(0, blockLength).ToArray();
    }

    private IBlockCompressor GetCompressorForCompression(CompressionMethod method, CreateArchiveOptions options)
    {
        if (method == CompressionMethod.LzmaMax &&
            options.LzmaDictionarySizeBytes is { } dictionarySize)
        {
            return _compressorRegistry.GetLzmaCompressor(dictionarySize, options.LzmaFastBytes);
        }
        if (method == CompressionMethod.ZstdHigh &&
            options.ZstdWindowLog is { } windowLog)
        {
            return _compressorRegistry.GetZstdCompressor(
                method,
                level: options.ZstdLevel,
                windowLog: windowLog,
                enableLongDistanceMatching: options.ZstdLongDistanceMatching);
        }

        return _compressorRegistry.GetCompressor(method);
    }

    private static byte[] BuildExtremeSample(ReadOnlySpan<byte> blockData)
    {
        const int windowSize = 64 * 1024;
        if (blockData.Length <= windowSize * 3)
        {
            return blockData.ToArray();
        }

        var sample = new byte[windowSize * 3];
        blockData[..windowSize].CopyTo(sample);
        var middleStart = (blockData.Length / 2) - (windowSize / 2);
        blockData.Slice(middleStart, windowSize).CopyTo(sample.AsSpan(windowSize));
        blockData[^windowSize..].CopyTo(sample.AsSpan(windowSize * 2));
        return sample;
    }

    private CompressionMethod SelectPreferredMethod(
        CreateArchiveOptions options,
        CompressionAnalysis analysis,
        ReadOnlySpan<byte> sample)
    {
        var mode = options.Mode;
        if (mode == CompressionMode.Fast)
        {
            return SelectFastMethod(analysis);
        }

        var bestMethod = CompressionMethod.Raw;
        var bestScore = double.MinValue;
        foreach (var candidate in _adaptiveCompressionEngine.GetCandidates(mode, analysis, options.TotalInputSizeBytes).Distinct())
        {
            if (candidate == CompressionMethod.Raw)
            {
                continue;
            }

            try
            {
                var compressedSample = GetCompressorForSampling(candidate, options).Compress(sample);
                var ratio = (double)compressedSample.Length / sample.Length;
                if (ratio >= 1.0)
                {
                    continue;
                }

                var score = _adaptiveCompressionEngine.Score(
                    mode,
                    candidate,
                    analysis,
                    ratioAfterCompression: ratio,
                    estimatedRelativeSpeed: EstimateRelativeSpeed(candidate),
                    estimatedRelativeMemoryUse: EstimateRelativeMemoryUse(candidate));

                if (analysis.LikelyAlreadyCompressed && ratio > 0.98)
                {
                    score -= 0.20;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMethod = candidate;
                }
            }
            catch
            {
                // Skip unavailable methods and continue evaluating.
            }
        }

        return bestMethod;
    }

    private IBlockCompressor GetCompressorForSampling(CompressionMethod method, CreateArchiveOptions options)
    {
        if (method == CompressionMethod.LzmaMax &&
            options.Mode == CompressionMode.Extreme)
        {
            var sampleDictionarySize = Math.Min(options.LzmaDictionarySizeBytes ?? 4 * 1024 * 1024, 4 * 1024 * 1024);
            return _compressorRegistry.GetLzmaCompressor(sampleDictionarySize, Math.Min(options.LzmaFastBytes, 128));
        }

        return _compressorRegistry.GetCompressor(method);
    }

    private static CompressionMethod SelectFastMethod(CompressionAnalysis analysis)
    {
        if (analysis.LikelyAlreadyCompressed ||
            analysis.FileTypeCategory is FileTypeCategory.Image or FileTypeCategory.Video or FileTypeCategory.Audio or FileTypeCategory.Archive)
        {
            return CompressionMethod.Raw;
        }

        return CompressionMethod.Lz4Fast;
    }

    private static double EstimateRelativeSpeed(CompressionMethod method)
    {
        return method switch
        {
            CompressionMethod.Raw => 1.00,
            CompressionMethod.Lz4Fast => 0.95,
            CompressionMethod.ZstdFast => 0.80,
            CompressionMethod.ZstdBalanced => 0.62,
            CompressionMethod.ZstdHigh => 0.42,
            CompressionMethod.LzmaMax => 0.25,
            CompressionMethod.DeflateFallback => 0.55,
            CompressionMethod.Blosc2 => 0.88,
            CompressionMethod.Zpaq => 0.05,
            CompressionMethod.Bsc => 0.35,
            CompressionMethod.Cmix => 0.02,
            _ => 0.50
        };
    }

    private static double EstimateRelativeMemoryUse(CompressionMethod method)
    {
        return method switch
        {
            CompressionMethod.Raw => 0.05,
            CompressionMethod.Lz4Fast => 0.15,
            CompressionMethod.ZstdFast => 0.28,
            CompressionMethod.ZstdBalanced => 0.40,
            CompressionMethod.ZstdHigh => 0.60,
            CompressionMethod.LzmaMax => 0.72,
            CompressionMethod.DeflateFallback => 0.33,
            CompressionMethod.Blosc2 => 0.30,
            CompressionMethod.Zpaq => 0.85,
            CompressionMethod.Bsc => 0.78,
            CompressionMethod.Cmix => 0.95,
            _ => 0.40
        };
    }

    private static string GetParentRelative(string relativePath)
    {
        var normalized = PathSecurity.NormalizeArchivePath(relativePath);
        var idx = normalized.LastIndexOf('/');
        return idx <= 0 ? string.Empty : normalized[..idx];
    }

    private sealed record PreparedSolidBlock(BlockEntryRecord Block, byte[] Bytes);

    private sealed class PreparedIndependentBlock
    {
        public PreparedIndependentBlock(
            FileEntryRecord fileEntry,
            int originalBlockSize,
            CompressionMethod method,
            byte[] compressedBytes,
            bool isRaw)
        {
            FileEntry = fileEntry;
            OriginalBlockSize = originalBlockSize;
            Method = method;
            CompressedBytes = compressedBytes;
            IsRaw = isRaw;
        }

        public FileEntryRecord FileEntry { get; }
        public int OriginalBlockSize { get; }
        public CompressionMethod Method { get; }
        public byte[] CompressedBytes { get; }
        public bool IsRaw { get; }
        public bool IsBcj { get; set; }
        
        public string? OriginalSha256 { get; set; }
        public bool IsDuplicate { get; set; }
        public string? DuplicateOfHash { get; set; }
    }
}
