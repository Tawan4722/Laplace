namespace Laplace.Core.Security;

public static class PathSecurity
{
    public static string NormalizeArchivePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized;
    }

    public static string EnsureSafeExtractionPath(string destinationRoot, string archiveRelativePath)
    {
        var normalizedRelative = NormalizeArchivePath(archiveRelativePath);
        if (string.IsNullOrWhiteSpace(normalizedRelative))
        {
            throw new InvalidDataException("Archive entry path is empty.");
        }

        if (Path.IsPathRooted(normalizedRelative))
        {
            throw new InvalidDataException($"Absolute archive path is not allowed: {archiveRelativePath}");
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
}
