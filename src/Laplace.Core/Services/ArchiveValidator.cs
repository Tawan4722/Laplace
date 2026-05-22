using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Enums;

namespace Laplace.Core.Services;

public static class ArchiveValidator
{
    public static void ValidateHeader(ArchiveHeader header, long archiveLength)
    {
        if (header.FormatVersion != 1)
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
    }

    public static void ValidateBlockEntries(IEnumerable<BlockEntryRecord> blocks, long archiveLength)
    {
        foreach (var block in blocks)
        {
            if (block.DataOffset < 0 || block.CompressedBlockSize < 0 || block.OriginalBlockSize < 0)
            {
                throw new LaplaceArchiveException($"Invalid block sizing for block #{block.BlockId}.");
            }

            if (!Enum.IsDefined(typeof(CompressionMethod), block.CompressionMethod))
            {
                throw new LaplaceArchiveException($"Unknown compression method ID in block #{block.BlockId}.");
            }

            var end = block.DataOffset + block.CompressedBlockSize;
            if (end > archiveLength)
            {
                throw new LaplaceArchiveException($"Block #{block.BlockId} extends beyond archive end.");
            }
        }
    }
}
