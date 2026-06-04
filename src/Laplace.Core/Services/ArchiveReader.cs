using Laplace.Core.Exceptions;
using Laplace.Core.Models;

namespace Laplace.Core.Services;

public sealed class ArchiveReader
{
    public ArchiveDocument Read(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ArchiveFormatCodec.ReadHeader(stream);
        ArchiveValidator.ValidateHeader(header, stream.Length);

        stream.Position = header.FileTableOffset;
        var files = ArchiveFormatCodec.ReadFileEntries(stream, header.FileEntryCount, header.FormatVersion);
        stream.Position = header.BlockTableOffset;
        var blocks = ArchiveFormatCodec.ReadBlockEntries(stream, header.BlockEntryCount, header.FormatVersion);
        ArchiveValidator.ValidateBlockEntries(blocks, stream.Length, header);

        return new ArchiveDocument
        {
            Header = header,
            FileEntries = files,
            BlockEntries = blocks
        };
    }

    public IReadOnlyList<FileEntryRecord> ListEntries(string archivePath)
    {
        return Read(archivePath).FileEntries;
    }

    public ArchiveHeader ReadHeaderOnly(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ArchiveFormatCodec.ReadHeader(stream);
        ArchiveValidator.ValidateHeader(header, stream.Length);
        return header;
    }

    public static Dictionary<long, List<BlockEntryRecord>> BuildBlockLookup(ArchiveDocument archive)
    {
        if (archive.Header.IsSolid)
        {
            throw new LaplaceArchiveException("Block lookup by owning file is not valid for solid LPC archives.");
        }

        var map = new Dictionary<long, List<BlockEntryRecord>>();
        foreach (var block in archive.BlockEntries)
        {
            if (!map.TryGetValue(block.OwningFileEntryId, out var list))
            {
                list = [];
                map[block.OwningFileEntryId] = list;
            }
            list.Add(block);
        }

        return map;
    }

    public static void ValidateEntryBlockReferences(ArchiveDocument archive)
    {
        var maxBlockIndex = archive.BlockEntries.Count - 1;
        if (archive.Header.IsSolid)
        {
            long previousEnd = 0;
            var orderedFiles = archive.FileEntries.Where(f => !f.IsDirectory)
                .OrderBy(f => f.DataStreamOffset)
                .ThenBy(f => f.EntryId)
                .ToList();

            foreach (var file in orderedFiles)
            {
                if (file.DataStreamOffset < 0)
                {
                    throw new LaplaceArchiveException($"Invalid data stream offset for file entry {file.RelativePath}.");
                }

                if (file.DataStreamOffset < previousEnd)
                {
                    throw new LaplaceArchiveException($"Solid file ranges overlap or are out of order for {file.RelativePath}.");
                }

                if (file.OriginalSize > 0)
                {
                    if (file.FirstBlockIndex < 0 || file.BlockCount <= 0)
                    {
                        throw new LaplaceArchiveException($"Solid file entry {file.RelativePath} does not reference its covering block range.");
                    }

                    var endIndex = file.FirstBlockIndex + file.BlockCount - 1;
                    if (endIndex > maxBlockIndex)
                    {
                        throw new LaplaceArchiveException($"Solid block range out of bounds for file entry {file.RelativePath}.");
                    }
                }

                previousEnd = file.DataStreamOffset + file.OriginalSize;
            }

            return;
        }

        foreach (var file in archive.FileEntries.Where(f => !f.IsDirectory))
        {
            if (file.FirstBlockIndex < 0 && file.BlockCount > 0)
            {
                throw new LaplaceArchiveException($"Invalid first block index for file entry {file.RelativePath}.");
            }

            if (file.BlockCount < 0)
            {
                throw new LaplaceArchiveException($"Negative block count for file entry {file.RelativePath}.");
            }

            if (file.BlockCount == 0)
            {
                continue;
            }

            var endIndex = file.FirstBlockIndex + file.BlockCount - 1;
            if (endIndex > maxBlockIndex)
            {
                throw new LaplaceArchiveException($"Block range out of bounds for file entry {file.RelativePath}.");
            }
        }
    }
}
