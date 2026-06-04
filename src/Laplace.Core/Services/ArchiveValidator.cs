using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Enums;

namespace Laplace.Core.Services;

public static class ArchiveValidator
{
    public static void ValidateHeader(ArchiveHeader header, long archiveLength)
    {
        if (header.FormatVersion is not (1 or 2 or 3 or 4))
        {
            throw new LaplaceArchiveException($"Unsupported LPC format version: {header.FormatVersion}");
        }

        if (header.DataSectionOffset <= 0 || header.DataSectionOffset > archiveLength)
        {
            throw new LaplaceArchiveException("Invalid data section offset.");
        }

        if (header.FileTableOffset <= 0 || header.FileTableOffset > archiveLength)
        {
            throw new LaplaceArchiveException("Invalid file table offset.");
        }

        if (header.BlockTableOffset <= 0 || header.BlockTableOffset > archiveLength)
        {
            throw new LaplaceArchiveException("Invalid block table offset.");
        }

        if (header.FileTableOffset < header.DataSectionOffset)
        {
            throw new LaplaceArchiveException("File table offset overlaps data section.");
        }

        if (header.BlockTableOffset < header.FileTableOffset)
        {
            throw new LaplaceArchiveException("Block table offset must be after file table.");
        }

        if (header.FileEntryCount < 0 || header.BlockEntryCount < 0)
        {
            throw new LaplaceArchiveException("Negative table counts are invalid.");
        }

        if (header.IsEncrypted)
        {
            if (header.FormatVersion < 2)
            {
                throw new LaplaceArchiveException("Encrypted archives require LPC format version 2.");
            }

            if (header.EncryptionAlgorithmId != ArchiveHeader.EncryptionAlgorithmAes256Gcm)
            {
                throw new LaplaceArchiveException($"Unsupported LPC encryption algorithm: {header.EncryptionAlgorithmId}");
            }

            if (header.EncryptionSalt.Length < ArchiveEncryption.MinimumSaltSizeBytes ||
                header.KeyDerivationIterations < CreateArchiveOptions.MinimumKeyDerivationIterations ||
                header.KeyDerivationIterations > CreateArchiveOptions.MaximumKeyDerivationIterations)
            {
                throw new LaplaceArchiveException("Invalid LPC encryption metadata.");
            }
        }

        if (header.IsSolid && header.FormatVersion < 4)
        {
            throw new LaplaceArchiveException("Solid LPC archives require format version 4.");
        }
    }

    public static void ValidateBlockEntries(IEnumerable<BlockEntryRecord> blocks, long archiveLength, ArchiveHeader? header = null)
    {
        var expectedBlockId = 0L;
        var expectedStreamOffset = 0L;
        foreach (var block in blocks)
        {
            if (block.BlockId != expectedBlockId)
            {
                throw new LaplaceArchiveException($"Unexpected block table order at block #{block.BlockId}.");
            }

            if (block.DataOffset < 0 || block.CompressedBlockSize < 0 || block.OriginalBlockSize < 0)
            {
                throw new LaplaceArchiveException($"Invalid block sizing for block #{block.BlockId}.");
            }

            if (!Enum.IsDefined(typeof(CompressionMethod), block.CompressionMethod))
            {
                throw new LaplaceArchiveException($"Unknown compression method ID in block #{block.BlockId}.");
            }

            if (header?.IsSolid == true)
            {
                if (block.OriginalStreamOffset != expectedStreamOffset)
                {
                    throw new LaplaceArchiveException($"Unexpected solid stream offset at block #{block.BlockId}.");
                }

                expectedStreamOffset += block.OriginalBlockSize;
            }

            var end = block.DataOffset + block.CompressedBlockSize;
            if (end > archiveLength)
            {
                throw new LaplaceArchiveException($"Block #{block.BlockId} extends beyond archive end.");
            }

            if (header?.IsEncrypted == true &&
                (block.EncryptionNonce.Length != ArchiveEncryption.NonceSizeBytes ||
                 block.EncryptionTag.Length != ArchiveEncryption.TagSizeBytes))
            {
                throw new LaplaceArchiveException($"Invalid encryption metadata for block #{block.BlockId}.");
            }

            expectedBlockId++;
        }
    }
}
