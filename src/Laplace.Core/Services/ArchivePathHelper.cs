namespace Laplace.Core.Services;

public static class ArchivePathHelper
{
    public static string ResolveBesideArchivePath(string inputPath)
    {
        var fullPath = Path.GetFullPath(inputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            parent = Directory.GetCurrentDirectory();
        }

        var name = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath).Name
            : Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "archive";
        }

        return GetAvailableArchivePath(Path.Combine(parent, $"{name}.lpc"));
    }

    public static string ResolveDefaultArchivePath(IEnumerable<string> inputPaths, string fallbackDirectory)
    {
        var paths = inputPaths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(Path.GetFullPath)
            .ToArray();

        if (paths.Length == 1)
        {
            return ResolveBesideArchivePath(paths[0]);
        }

        var directory = FindCommonParentDirectory(paths);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = fallbackDirectory;
        }

        return GetAvailableArchivePath(Path.Combine(directory, "archive.lpc"));
    }

    public static string GetAvailableArchivePath(string preferredPath)
    {
        if (!File.Exists(preferredPath) && !Directory.Exists(preferredPath))
        {
            return preferredPath;
        }

        var directory = Path.GetDirectoryName(preferredPath) ?? Directory.GetCurrentDirectory();
        var name = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find an available archive name beside: {preferredPath}");
    }

    private static string? FindCommonParentDirectory(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        var parents = paths
            .Select(path => Directory.Exists(path)
                ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))
                : Path.GetDirectoryName(path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .ToArray();

        if (parents.Length == 0)
        {
            return null;
        }

        var common = parents[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var parent in parents.Skip(1))
        {
            var candidate = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrEmpty(common) &&
                   !candidate.Equals(common, StringComparison.OrdinalIgnoreCase) &&
                   !candidate.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                common = Path.GetDirectoryName(common);
            }
        }

        return string.IsNullOrWhiteSpace(common) ? null : common;
    }
}
