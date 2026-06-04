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
        var scanned = ArchivePathScanner.Scan(inputPaths);
        if (scanned.Count == 0)
        {
            throw new InvalidOperationException("No input files or folders were found.");
        }

        var sorted = scanned
            .OrderBy(x => x.RelativePath.Count(c => c == '/'))
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputArchivePath))!);

        var header = new ArchiveHeader
        {
            CreatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DefaultBlockSize = (uint)options.BlockSizeBytes,
            Comment = options.Comment
        };
        var useSolid = ShouldUseSolidMode(options, sorted);
        if (options.EncryptMetadata)
        {
            throw new NotSupportedException("LPC metadata encryption is reserved for LPCv3 but is not implemented yet.");
        }

        if (options.VolumeSizeBytes is not null)
        {
            throw new NotSupportedException("LPC multi-volume output is reserved for LPCv3 but is not implemented yet.");
        }

        if (options.RecoveryPercent > 0)
        {
            throw new NotSupportedException("LPC recovery records are reserved for LPCv3 but are not implemented yet.");
        }

        if (options.LockArchive)
        {
            header.FormatVersion = 3;
            header.ArchiveFlags |= ArchiveHeader.LockedFlag;
        }

        if (useSolid)
        {
            header.FormatVersion = Math.Max(header.FormatVersion, (ushort)4);
            header.ArchiveFlags |= ArchiveHeader.SolidFlag;
        }

        var encryptionKey = Array.Empty<byte>();
        if (options.Password is not null)
        {
            if (options.KeyDerivationIterations < CreateArchiveOptions.MinimumKeyDerivationIterations ||
                options.KeyDerivationIterations > CreateArchiveOptions.MaximumKeyDerivationIterations)
            {
                throw new InvalidOperationException(
                    $"Key derivation iterations must be between {CreateArchiveOptions.MinimumKeyDerivationIterations:N0} and {CreateArchiveOptions.MaximumKeyDerivationIterations:N0}.");
            }

            header.FormatVersion = Math.Max(header.FormatVersion, (ushort)2);
            header.ArchiveFlags |= ArchiveHeader.EncryptionFlag;
            header.EncryptionAlgorithmId = ArchiveHeader.EncryptionAlgorithmAes256Gcm;
            header.KeyDerivationIterations = options.KeyDerivationIterations;
            header.EncryptionSalt = ArchiveEncryption.CreateSalt();
            encryptionKey = ArchiveEncryption.DeriveKey(options.Password, header.EncryptionSalt, header.KeyDerivationIterations);
        }

        try
        {
            var fileEntries = new List<FileEntryRecord>(sorted.Count);
            var blockEntries = new List<BlockEntryRecord>();
            var directoryIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var totalBytes = sorted.Where(x => !x.IsDirectory).Sum(x => new FileInfo(x.FullPath).Length);

            await using var archiveStream = new FileStream(
                outputArchivePath,
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
            ArchiveFormatCodec.WriteFileEntries(archiveStream, fileEntries, header.FormatVersion);
            header.BlockTableOffset = archiveStream.Position;
            ArchiveFormatCodec.WriteBlockEntries(archiveStream, blockEntries, header.FormatVersion);

            archiveStream.Position = 0;
            ArchiveFormatCodec.WriteHeader(archiveStream, header);
            await archiveStream.FlushAsync(cancellationToken).ConfigureAwait(false);

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
        FileStream archiveStream,
        List<FileEntryRecord> fileEntries,
        List<BlockEntryRecord> blockEntries,
        Dictionary<string, long> directoryIds,
        long totalBytes,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long processedBytes = 0;

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

            fileEntry.FirstBlockIndex = blockEntries.Count;
            using var fs = new FileStream(
                sourceEntry.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                options.BlockSizeBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var methodsUsed = new HashSet<CompressionMethod>();
            var buffer = new byte[options.BlockSizeBytes];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                incrementalHash.AppendData(buffer, 0, bytesRead);
                var (outputMethod, outputBytes, isRaw) = SelectAndCompressBlock(sourceEntry.RelativePath, options.Mode, buffer, bytesRead);
                var compressor = _compressorRegistry.GetCompressor(outputMethod);
                methodsUsed.Add(outputMethod);

                var block = new BlockEntryRecord
                {
                    BlockId = blockEntries.Count,
                    OwningFileEntryId = fileEntry.EntryId,
                    OriginalBlockSize = bytesRead,
                    CompressedBlockSize = outputBytes.Length,
                    CompressionMethod = outputMethod,
                    CompressionLevel = compressor.Level,
                    Flags = isRaw ? 1u : 0u,
                    IsRaw = isRaw
                };

                if (header.IsEncrypted)
                {
                    outputBytes = ArchiveEncryption.EncryptBlock(outputBytes, encryptionKey, block);
                }

                block.DataOffset = archiveStream.Position;
                block.BlockChecksumCrc32C = ChecksumService.ComputeCrc32C(outputBytes);
                await archiveStream.WriteAsync(outputBytes, cancellationToken).ConfigureAwait(false);
                blockEntries.Add(block);

                fileEntry.CompressedSize += outputBytes.Length;
                fileEntry.OriginalSize += bytesRead;
                fileEntry.BlockCount++;
                processedBytes += bytesRead;

                progress?.Report(new ArchiveOperationProgress
                {
                    CurrentItem = sourceEntry.RelativePath,
                    ProcessedBytes = processedBytes,
                    TotalBytes = totalBytes,
                    Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                });
            }

            fileEntry.FileChecksum = incrementalHash.GetHashAndReset();
            fileEntry.CompressionSummary = string.Join(",", methodsUsed.OrderBy(x => (int)x));
            fileEntries.Add(fileEntry);
        }
    }

    private async Task WriteSolidEntriesAsync(
        IReadOnlyList<InputEntry> sorted,
        CreateArchiveOptions options,
        ArchiveHeader header,
        byte[] encryptionKey,
        FileStream archiveStream,
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
        var solidBuffer = new byte[options.BlockSizeBytes];
        var solidBufferLength = 0;
        var blockHintPath = string.Empty;
        var readBuffer = new byte[Math.Min(options.BlockSizeBytes, 256 * 1024)];

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
                    var toCopy = Math.Min(solidBuffer.Length - solidBufferLength, bytesRead - consumed);
                    readBuffer.AsSpan(consumed, toCopy).CopyTo(solidBuffer.AsSpan(solidBufferLength, toCopy));
                    blockHintPath = sourceEntry.RelativePath;
                    solidBufferLength += toCopy;
                    consumed += toCopy;
                    solidStreamOffset += toCopy;
                    fileEntry.OriginalSize += toCopy;
                    processedBytes += toCopy;

                    if (solidBufferLength == solidBuffer.Length)
                    {
                        await FlushSolidBlockAsync(blockHintPath, options.Mode, header, encryptionKey, solidBuffer, solidBufferLength, blockStreamOffset, archiveStream, blockEntries, cancellationToken).ConfigureAwait(false);
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
            await FlushSolidBlockAsync(blockHintPath, options.Mode, header, encryptionKey, solidBuffer, solidBufferLength, blockStreamOffset, archiveStream, blockEntries, cancellationToken).ConfigureAwait(false);
        }

        FinalizeSolidFileEntries(fileEntries, blockEntries);
    }

    private async Task FlushSolidBlockAsync(
        string blockHintPath,
        CompressionMode mode,
        ArchiveHeader header,
        byte[] encryptionKey,
        byte[] solidBuffer,
        int solidBufferLength,
        long originalStreamOffset,
        FileStream archiveStream,
        List<BlockEntryRecord> blockEntries,
        CancellationToken cancellationToken)
    {
        var (outputMethod, outputBytes, isRaw) = SelectAndCompressBlock(blockHintPath, mode, solidBuffer, solidBufferLength);
        var compressor = _compressorRegistry.GetCompressor(outputMethod);
        var block = new BlockEntryRecord
        {
            BlockId = blockEntries.Count,
            OwningFileEntryId = -1,
            OriginalBlockSize = solidBufferLength,
            CompressedBlockSize = outputBytes.Length,
            CompressionMethod = outputMethod,
            CompressionLevel = compressor.Level,
            OriginalStreamOffset = originalStreamOffset,
            Flags = isRaw ? 1u : 0u,
            IsRaw = isRaw
        };

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
            _ => fileCount > 1 && options.Mode is CompressionMode.Balanced or CompressionMode.Maximum or CompressionMode.Intensive or CompressionMode.Compressed
        };
    }

    private byte[] CompressBlock(CompressionMethod preferredMethod, ReadOnlySpan<byte> blockData)
    {
        var compressor = _compressorRegistry.GetCompressor(preferredMethod);
        return compressor.Compress(blockData);
    }

    private (CompressionMethod Method, byte[] Bytes, bool IsRaw) SelectAndCompressBlock(
        string relativePath,
        CompressionMode mode,
        byte[] blockBuffer,
        int blockLength)
    {
        var blockData = blockBuffer.AsSpan(0, blockLength);
        if (blockData.Length == 0)
        {
            return (CompressionMethod.Raw, [], true);
        }

        var sample = blockData[..Math.Min(blockData.Length, 64 * 1024)];
        var analysis = _adaptiveCompressionEngine.Analyze(relativePath, sample);
        var bestMethod = SelectPreferredMethod(mode, analysis, sample);

        if (bestMethod == CompressionMethod.Raw)
        {
            return (CompressionMethod.Raw, blockData.ToArray(), true);
        }

        byte[] compressed;
        try
        {
            compressed = CompressBlock(bestMethod, blockData);
        }
        catch
        {
            return (CompressionMethod.Raw, blockData.ToArray(), true);
        }

        if (compressed.Length >= blockData.Length)
        {
            return (CompressionMethod.Raw, blockData.ToArray(), true);
        }

        return (bestMethod, compressed, false);
    }

    private CompressionMethod SelectPreferredMethod(CompressionMode mode, CompressionAnalysis analysis, ReadOnlySpan<byte> sample)
    {
        if (mode == CompressionMode.Fast)
        {
            return SelectFastMethod(analysis);
        }

        var bestMethod = CompressionMethod.Raw;
        var bestScore = double.MinValue;
        foreach (var candidate in _adaptiveCompressionEngine.GetCandidates(mode, analysis).Distinct())
        {
            if (candidate == CompressionMethod.Raw)
            {
                continue;
            }

            try
            {
                var compressedSample = _compressorRegistry.GetCompressor(candidate).Compress(sample);
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
            _ => 0.40
        };
    }

    private static string GetParentRelative(string relativePath)
    {
        var normalized = PathSecurity.NormalizeArchivePath(relativePath);
        var idx = normalized.LastIndexOf('/');
        return idx <= 0 ? string.Empty : normalized[..idx];
    }
}
