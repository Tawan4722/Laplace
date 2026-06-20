namespace Laplace.Core.Services;

public static class ArchiveVolumePathHelper
{
    public static IReadOnlyList<string> FindVolumes(string outputArchivePath)
    {
        var fullOutputPath = Path.GetFullPath(outputArchivePath);
        var directory = Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory();
        var extension = Path.GetExtension(fullOutputPath);

        if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lpc", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(fullOutputPath);
            return Directory.EnumerateFiles(directory, $"{fileName}.*")
                .Select(path => new { Path = path, Index = ParseSevenZipVolumeIndex(path) })
                .Where(item => item.Index is not null)
                .OrderBy(item => item.Index)
                .Select(item => item.Path)
                .ToArray();
        }

        if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(fullOutputPath);
            return Directory.EnumerateFiles(directory, $"{baseName}.part*.rar")
                .Select(path => new { Path = path, Index = ParseRarVolumeIndex(path) })
                .Where(item => item.Index is not null)
                .OrderBy(item => item.Index)
                .Select(item => item.Path)
                .ToArray();
        }

        return [];
    }

    public static void DeleteExistingVolumes(string outputArchivePath)
    {
        foreach (var volumePath in FindVolumes(outputArchivePath))
        {
            File.Delete(volumePath);
        }
    }

    private static int? ParseSevenZipVolumeIndex(string path)
    {
        return int.TryParse(Path.GetExtension(path).TrimStart('.'), out var index) ? index : null;
    }

    private static int? ParseRarVolumeIndex(string path)
    {
        var fileName = Path.GetFileName(path);
        var partMarker = fileName.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        var rarMarker = fileName.LastIndexOf(".rar", StringComparison.OrdinalIgnoreCase);
        return partMarker >= 0 &&
               rarMarker > partMarker + 5 &&
               int.TryParse(fileName.AsSpan(partMarker + 5, rarMarker - partMarker - 5), out var index)
            ? index
            : null;
    }
}
