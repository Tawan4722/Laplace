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
        var blocksByFileId = ArchiveReader.BuildBlockLookup(archive);
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
            await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                if (!blocksByFileId.TryGetValue(file.EntryId, out var fileBlocks))
                {
                    fileBlocks = [];
                }

                foreach (var block in fileBlocks.OrderBy(b => b.BlockId))
                {
                    archiveStream.Position = block.DataOffset;
                    var compressedBytes = new byte[block.CompressedBlockSize];
                    var read = await archiveStream.ReadAsync(compressedBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read != compressedBytes.Length)
                    {
                        return TestArchiveResult.Failed($"Unexpected EOF at block #{block.BlockId}.");
                    }

                    if (ChecksumService.ComputeCrc32C(compressedBytes) != block.BlockChecksumCrc32C)
                    {
                        return TestArchiveResult.Failed($"CRC32C mismatch at block #{block.BlockId}.");
                    }

                    if (archive.Header.IsEncrypted)
                    {
                        try
                        {
                            compressedBytes = ArchiveEncryption.DecryptBlock(compressedBytes, encryptionKey, block);
                        }
                        catch (CryptographicException)
                        {
                            return TestArchiveResult.Failed($"Invalid password or corrupted encrypted block #{block.BlockId}.");
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
                        return TestArchiveResult.Failed($"Unexpected decompressed size at block #{block.BlockId}.");
                    }

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
