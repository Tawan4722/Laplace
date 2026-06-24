namespace Laplace.Core.Services;

internal static class GlobFilter
{
    public static bool IsIncluded(
        string relativePath,
        bool isDirectory,
        IReadOnlyList<string>? includePatterns,
        IReadOnlyList<string>? excludePatterns)
    {
        var normalizedPath = relativePath.Replace('\\', '/');

        if (includePatterns is { Count: > 0 })
        {
            if (!isDirectory && !includePatterns.Any(pattern => MatchesPattern(normalizedPath, pattern)))
            {
                return false;
            }
        }

        if (excludePatterns is { Count: > 0 })
        {
            if (excludePatterns.Any(pattern => MatchesPattern(normalizedPath, pattern)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        var normalizedPattern = pattern.Replace('\\', '/');
        var normalizedPath = path.Replace('\\', '/');

        // Filename-only match if the pattern has no directory separators
        if (!normalizedPattern.Contains('/'))
        {
            var fileName = Path.GetFileName(normalizedPath);
            if (!string.IsNullOrEmpty(fileName))
            {
                var regexPattern = ConvertGlobToRegex(normalizedPattern, isRelative: false);
                var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (regex.IsMatch(fileName))
                {
                    return true;
                }
            }
        }

        // Full path match
        {
            var isRelative = !normalizedPattern.StartsWith('/') && !normalizedPattern.StartsWith('*');
            var regexPattern = ConvertGlobToRegex(normalizedPattern, isRelative);
            var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (regex.IsMatch(normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    private static string ConvertGlobToRegex(string glob, bool isRelative)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("^");
        if (isRelative)
        {
            sb.Append("(?:.*/)?");
        }
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                // Check if it's "**"
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    // Single "*" matches anything except "/"
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if (c == '/' || c == '.' || c == '$' || c == '^' || c == '{' || c == '}' || c == '(' || c == ')' || c == '+' || c == '[' || c == ']' || c == '|' || c == '\\')
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append("$");
        return sb.ToString();
    }

    public static List<InputEntry> Apply(List<InputEntry> entries,
        IReadOnlyList<string>? includePatterns,
        IReadOnlyList<string>? excludePatterns)
    {
        if (includePatterns is not { Count: > 0 } && excludePatterns is not { Count: > 0 })
        {
            return entries;
        }

        // First pass: determine which files survive filtering
        var survivingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory && IsIncluded(entry.RelativePath, false, includePatterns, excludePatterns))
            {
                survivingFiles.Add(entry.RelativePath);
            }
        }

        // Second pass: keep directories that have surviving file descendants
        var result = new List<InputEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                var dirPrefix = entry.RelativePath.Replace('\\', '/').TrimEnd('/') + "/";
                if (survivingFiles.Any(f => f.Replace('\\', '/').StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(entry);
                }
            }
            else if (survivingFiles.Contains(entry.RelativePath))
            {
                result.Add(entry);
            }
        }

        return result;
    }
}
