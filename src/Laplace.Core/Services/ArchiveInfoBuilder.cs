using Laplace.Core.Models;

namespace Laplace.Core.Services;

public static class ArchiveInfoBuilder
{
    public static ArchiveInfo Build(ArchiveDocument archive)
    {
        var files = archive.FileEntries.Where(x => !x.IsDirectory).ToList();
        var folders = archive.FileEntries.Count - files.Count;
        var originalSize = files.Sum(x => x.OriginalSize);
        var compressedSize = files.Sum(x => x.CompressedSize);
        var methods = archive.BlockEntries
            .Select(b => b.CompressionMethod.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ArchiveInfo
        {
            ArchiveVersion = archive.Header.FormatVersion,
            FileCount = files.Count,
            FolderCount = folders,
            BlockCount = archive.BlockEntries.Count,
            OriginalSize = originalSize,
            CompressedSize = compressedSize,
            Ratio = originalSize == 0 ? 1 : (double)compressedSize / originalSize,
            MethodsUsed = methods,
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(archive.Header.CreatedUnixMilliseconds).UtcDateTime
        };
    }
}

public sealed class ArchiveInfo
{
    public int ArchiveVersion { get; init; }
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
    public int BlockCount { get; init; }
    public long OriginalSize { get; init; }
    public long CompressedSize { get; init; }
    public double Ratio { get; init; }
    public required string[] MethodsUsed { get; init; }
    public DateTime CreatedUtc { get; init; }
}
