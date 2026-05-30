namespace Laplace.Core.Security;

public static class PathSecurity
{
    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public static string NormalizeArchivePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized;
    }

    public static string EnsureSafeExtractionPath(string destinationRoot, string archiveRelativePath)
    {
        var normalizedRelative = ValidateAndNormalizeRelativePath(archiveRelativePath);
        if (string.IsNullOrWhiteSpace(normalizedRelative))
        {
            throw new InvalidDataException("Archive entry path is empty.");
        }

        var fullRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var combinedPath = Path.GetFullPath(Path.Combine(fullRoot, normalizedRelative));
        if (!combinedPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path traversal detected: {archiveRelativePath}");
        }

        return combinedPath;
    }

    public static void EnsureNoReparsePointInPath(string destinationRoot, string fullDestinationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fullRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(fullDestinationPath);
        var current = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);

        while (!string.IsNullOrWhiteSpace(current) &&
               current.Length >= fullRoot.Length &&
               current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"Extraction path crosses a reparse point: {current}");
            }

            if (string.Equals(
                    current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    fullRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static string ValidateAndNormalizeRelativePath(string archiveRelativePath)
    {
        if (string.IsNullOrWhiteSpace(archiveRelativePath))
        {
            throw new InvalidDataException("Archive entry path is empty.");
        }

        if (archiveRelativePath.IndexOf('\0') >= 0 ||
            archiveRelativePath.Any(char.IsControl))
        {
            throw new InvalidDataException("Archive entry path contains invalid control characters.");
        }

        var normalized = archiveRelativePath.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            Path.IsPathRooted(archiveRelativePath) ||
            Path.IsPathFullyQualified(archiveRelativePath) ||
            LooksLikeWindowsDrivePath(normalized))
        {
            throw new InvalidDataException($"Absolute archive path is not allowed: {archiveRelativePath}");
        }

        normalized = normalized.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidDataException("Archive entry path is empty.");
        }

        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            ValidateSegment(segment, archiveRelativePath);
        }

        return normalized;
    }

    private static void ValidateSegment(string segment, string originalPath)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment is "." or "..")
        {
            throw new InvalidDataException($"Unsafe archive path segment in: {originalPath}");
        }

        if (segment.EndsWith(" ", StringComparison.Ordinal) ||
            segment.EndsWith(".", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive path segment has an unsafe Windows name: {originalPath}");
        }

        if (segment.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive path segment contains an alternate stream or drive separator: {originalPath}");
        }

        var baseName = segment.Split('.')[0];
        if (WindowsReservedDeviceNames.Contains(baseName))
        {
            throw new InvalidDataException($"Archive path segment uses a reserved Windows device name: {originalPath}");
        }
    }

    private static bool LooksLikeWindowsDrivePath(string normalizedPath)
    {
        return normalizedPath.Length >= 2 &&
               char.IsLetter(normalizedPath[0]) &&
               normalizedPath[1] == ':';
    }
}
