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
        var files = ArchiveFormatCodec.ReadFileEntries(stream, header.FileEntryCount);
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
