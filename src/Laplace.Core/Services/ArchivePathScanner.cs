using Laplace.Core.Security;

namespace Laplace.Core.Services;

internal static class ArchivePathScanner
{
    public static List<InputEntry> Scan(
        IEnumerable<string> inputPaths,
        IReadOnlyList<string>? includePatterns = null,
        IReadOnlyList<string>? excludePatterns = null)
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
                var dirInfo = new DirectoryInfo(full);
                var rootName = dirInfo.Name;
                result.Add(new InputEntry(full, rootName, true));

                var parentPath = dirInfo.Parent?.FullName;
                foreach (var info in dirInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    var relative = parentPath is not null
                        ? Path.GetRelativePath(parentPath, info.FullName)
                        : Path.Combine(rootName, Path.GetRelativePath(full, info.FullName));
                    result.Add(new InputEntry(info.FullName, PathSecurity.NormalizeArchivePath(relative), info is DirectoryInfo));
                }
                continue;
            }

            throw new FileNotFoundException($"Input path does not exist: {inputPath}");
        }

        result = GlobFilter.Apply(result, includePatterns, excludePatterns);
        return result;
    }
}

internal sealed record InputEntry(string FullPath, string RelativePath, bool IsDirectory);
