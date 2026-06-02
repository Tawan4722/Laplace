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
        var encryptionKey = Array.Empty<byte>();
        if (options.Password is not null)
        {
            if (options.KeyDerivationIterations < CreateArchiveOptions.MinimumKeyDerivationIterations ||
                options.KeyDerivationIterations > CreateArchiveOptions.MaximumKeyDerivationIterations)
            {
                throw new InvalidOperationException(
                    $"Key derivation iterations must be between {CreateArchiveOptions.MinimumKeyDerivationIterations:N0} and {CreateArchiveOptions.MaximumKeyDerivationIterations:N0}.");
            }

            header.FormatVersion = 2;
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
            long processedBytes = 0;

            await using var archiveStream = new FileStream(
                outputArchivePath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            header.DataSectionOffset = ArchiveFormatCodec.WriteHeader(archiveStream, header);

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

            header.FileEntryCount = fileEntries.Count;
            header.BlockEntryCount = blockEntries.Count;
            header.FileTableOffset = archiveStream.Position;
            ArchiveFormatCodec.WriteFileEntries(archiveStream, fileEntries);
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
