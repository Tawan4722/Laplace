using Laplace.Core.Security;

namespace Laplace.Core.Services;

internal static class ArchivePathScanner
{
    public static List<InputEntry> Scan(IEnumerable<string> inputPaths)
    {
        var result = new List<InputEntry>();
        foreach (var inputPath in inputPaths)
        {
            var full = Path.GetFullPath(inputPath);
            if (File.Exists(full))
            {
                result.Add(new InputEntry(full, Path.GetFileName(full), false));
                continue;
            }

            if (Directory.Exists(full))
            {
                var rootName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(rootName))
                {
                    rootName = new DirectoryInfo(full).Name;
                }

                result.Add(new InputEntry(full, rootName, true));
                ScanDirectory(full, rootName, result);
                continue;
            }

            throw new FileNotFoundException($"Input path does not exist: {inputPath}");
        }

        return result;
    }

    private static void ScanDirectory(string fullPath, string relativePath, List<InputEntry> entries)
    {
        foreach (var directory in Directory.GetDirectories(fullPath))
        {
            var name = Path.GetFileName(directory);
            var relative = PathSecurity.NormalizeArchivePath(Path.Combine(relativePath, name));
            entries.Add(new InputEntry(directory, relative, true));
            ScanDirectory(directory, relative, entries);
        }

        foreach (var file in Directory.GetFiles(fullPath))
        {
            var name = Path.GetFileName(file);
            var relative = PathSecurity.NormalizeArchivePath(Path.Combine(relativePath, name));
            entries.Add(new InputEntry(file, relative, false));
        }
    }
}

internal sealed record InputEntry(string FullPath, string RelativePath, bool IsDirectory);
