using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using System.Security.Cryptography;

namespace Laplace.Core.Services;

public sealed class ArchiveTester
{
    private readonly ICompressorRegistry _compressorRegistry;
    private readonly ArchiveReader _archiveReader;

    public ArchiveTester(ICompressorRegistry compressorRegistry, ArchiveReader? archiveReader = null)
    {
        _compressorRegistry = compressorRegistry;
        _archiveReader = archiveReader ?? new ArchiveReader();
    }

    public async Task<TestArchiveResult> TestAsync(
        string archivePath,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await TestAsync(archivePath, password: null, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TestArchiveResult> TestAsync(
        string archivePath,
        PasswordContext? password,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var archive = _archiveReader.Read(archivePath);
        ArchiveReader.ValidateEntryBlockReferences(archive);
        var files = archive.FileEntries.Where(f => !f.IsDirectory).ToList();
        var totalBytes = files.Sum(f => f.OriginalSize);
        long processedBytes = 0;
        var encryptionKey = Array.Empty<byte>();
        if (archive.Header.IsEncrypted)
        {
            if (password is null)
            {
                return TestArchiveResult.Failed($"Archive requires a password: {archivePath}");
            }

            encryptionKey = ArchiveEncryption.DeriveKey(password, archive.Header.EncryptionSalt, archive.Header.KeyDerivationIterations);
        }

        try
        {
            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (archive.Header.IsSolid)
            {
                var solidResult = await TestSolidAsync(archive, archiveStream, totalBytes, encryptionKey, progress, cancellationToken).ConfigureAwait(false);
                if (!solidResult.Success)
                {
                    return solidResult;
                }

                processedBytes = totalBytes;
            }
            else
            {
                var blocksByFileId = ArchiveReader.BuildBlockLookup(archive);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    if (!blocksByFileId.TryGetValue(file.EntryId, out var fileBlocks))
                    {
                        fileBlocks = [];
                    }

                    foreach (var block in fileBlocks)
                    {
                        var readBlock = await ReadBlockAsync(archive, archiveStream, block, encryptionKey, cancellationToken).ConfigureAwait(false);
                        if (!readBlock.Success)
                        {
                            return TestArchiveResult.Failed(readBlock.ErrorMessage!);
                        }

                        var decompressed = readBlock.Data!;
                        hash.AppendData(decompressed);
                        processedBytes += decompressed.Length;
                        progress?.Report(new ArchiveOperationProgress
                        {
                            CurrentItem = file.RelativePath,
                            ProcessedBytes = processedBytes,
                            TotalBytes = totalBytes,
                            Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                        });
                    }

                    if (file.ChecksumType == ChecksumType.Sha256)
                    {
                        var fileHash = hash.GetHashAndReset();
                        if (!fileHash.SequenceEqual(file.FileChecksum))
                        {
                            return TestArchiveResult.Failed($"SHA-256 mismatch for file {file.RelativePath}.");
                        }
                    }
                }
            }

            return TestArchiveResult.Ok(files.Count, archive.BlockEntries.Count);
        }
        finally
        {
            if (encryptionKey.Length > 0)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    private async Task<TestArchiveResult> TestSolidAsync(
        ArchiveDocument archive,
        FileStream archiveStream,
        long totalBytes,
        byte[] encryptionKey,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var orderedFiles = archive.FileEntries.Where(f => !f.IsDirectory)
            .OrderBy(f => f.DataStreamOffset)
            .ThenBy(f => f.EntryId)
            .ToList();
        long logicalOffset = 0;
        long processedBytes = 0;
        var fileIndex = 0;
        var fileOffset = 0L;
        IncrementalHash? hash = null;
        FileEntryRecord? currentFile = null;

        try
        {
            while (fileIndex < orderedFiles.Count && orderedFiles[fileIndex].OriginalSize == 0)
            {
                var empty = orderedFiles[fileIndex++];
                if (empty.ChecksumType == ChecksumType.Sha256 && !SHA256.HashData([]).SequenceEqual(empty.FileChecksum))
                {
                    return TestArchiveResult.Failed($"SHA-256 mismatch for file {empty.RelativePath}.");
                }
            }

            foreach (var block in archive.BlockEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.OriginalStreamOffset != logicalOffset)
                {
                    return TestArchiveResult.Failed($"Unexpected solid stream offset at block #{block.BlockId}.");
                }

                var readBlock = await ReadBlockAsync(archive, archiveStream, block, encryptionKey, cancellationToken).ConfigureAwait(false);
                if (!readBlock.Success)
                {
                    return TestArchiveResult.Failed(readBlock.ErrorMessage!);
                }

                var decompressed = readBlock.Data!;
                var consumed = 0;
                while (consumed < decompressed.Length)
                {
                    if (fileIndex >= orderedFiles.Count)
                    {
                        return TestArchiveResult.Failed("Solid data stream contains more bytes than the file table describes.");
                    }

                    currentFile ??= orderedFiles[fileIndex];
                    hash ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var remainingInFile = currentFile.OriginalSize - fileOffset;
                    var take = (int)Math.Min(remainingInFile, decompressed.Length - consumed);
                    hash.AppendData(decompressed, consumed, take);
                    processedBytes += take;
                    logicalOffset += take;
                    fileOffset += take;
                    consumed += take;
                    progress?.Report(new ArchiveOperationProgress
                    {
                        CurrentItem = currentFile.RelativePath,
                        ProcessedBytes = processedBytes,
                        TotalBytes = totalBytes,
                        Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                    });

                    if (fileOffset == currentFile.OriginalSize)
                    {
                        if (currentFile.ChecksumType == ChecksumType.Sha256)
                        {
                            var fileHash = hash.GetHashAndReset();
                            if (!fileHash.SequenceEqual(currentFile.FileChecksum))
                            {
                                return TestArchiveResult.Failed($"SHA-256 mismatch for file {currentFile.RelativePath}.");
                            }
                        }

                        hash.Dispose();
                        hash = null;
                        currentFile = null;
                        fileIndex++;
                        fileOffset = 0;

                        while (fileIndex < orderedFiles.Count && orderedFiles[fileIndex].OriginalSize == 0)
                        {
                            var empty = orderedFiles[fileIndex++];
                            if (empty.ChecksumType == ChecksumType.Sha256 && !SHA256.HashData([]).SequenceEqual(empty.FileChecksum))
                            {
                                return TestArchiveResult.Failed($"SHA-256 mismatch for file {empty.RelativePath}.");
                            }
                        }
                    }
                }
            }

            if (fileIndex != orderedFiles.Count)
            {
                return TestArchiveResult.Failed("Solid archive ended before all file data could be reconstructed.");
            }

            return TestArchiveResult.Ok(orderedFiles.Count, archive.BlockEntries.Count);
        }
        finally
        {
            hash?.Dispose();
        }
    }

    private async Task<BlockReadResult> ReadBlockAsync(
        ArchiveDocument archive,
        FileStream archiveStream,
        BlockEntryRecord block,
        byte[] encryptionKey,
        CancellationToken cancellationToken)
    {
        archiveStream.Position = block.DataOffset;
        var compressedBytes = new byte[block.CompressedBlockSize];
        var read = await archiveStream.ReadAsync(compressedBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (read != compressedBytes.Length)
        {
            return BlockReadResult.Fail($"Unexpected EOF at block #{block.BlockId}.");
        }

        if (ChecksumService.ComputeCrc32C(compressedBytes) != block.BlockChecksumCrc32C)
        {
            return BlockReadResult.Fail($"CRC32C mismatch at block #{block.BlockId}.");
        }

        if (archive.Header.IsEncrypted)
        {
            try
            {
                compressedBytes = ArchiveEncryption.DecryptBlock(compressedBytes, encryptionKey, block);
            }
            catch (CryptographicException)
            {
                return BlockReadResult.Fail($"Invalid password or corrupted encrypted block #{block.BlockId}.");
            }
        }

        byte[] decompressed;
        if (block.CompressionMethod == CompressionMethod.Raw || block.IsRaw)
        {
            decompressed = compressedBytes;
        }
        else
        {
            decompressed = _compressorRegistry.GetCompressor(block.CompressionMethod).Decompress(compressedBytes, block.OriginalBlockSize);
        }

        if (decompressed.Length != block.OriginalBlockSize)
        {
            return BlockReadResult.Fail($"Unexpected decompressed size at block #{block.BlockId}.");
        }

        return BlockReadResult.Ok(decompressed);
    }
}

internal sealed class BlockReadResult
{
    private BlockReadResult(bool success, byte[]? data, string? errorMessage)
    {
        Success = success;
        Data = data;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public byte[]? Data { get; }
    public string? ErrorMessage { get; }

    public static BlockReadResult Ok(byte[] data) => new(true, data, null);
    public static BlockReadResult Fail(string errorMessage) => new(false, null, errorMessage);
}

public sealed class TestArchiveResult
{
    private TestArchiveResult(bool success, string message, int fileCount, int blockCount)
    {
        Success = success;
        Message = message;
        FileCount = fileCount;
        BlockCount = blockCount;
    }

    public bool Success { get; }
    public string Message { get; }
    public int FileCount { get; }
    public int BlockCount { get; }

    public static TestArchiveResult Ok(int fileCount, int blockCount) => new(true, "Archive integrity OK.", fileCount, blockCount);
    public static TestArchiveResult Failed(string reason) => new(false, reason, 0, 0);
}
