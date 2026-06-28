namespace Laplace.Core.Services;

internal static class GlobFilter
{
    private sealed class CompiledPattern
    {
        public System.Text.RegularExpressions.Regex Regex { get; }
        public System.Text.RegularExpressions.Regex? FileNameRegex { get; }

        public CompiledPattern(string pattern)
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            if (!normalizedPattern.Contains('/'))
            {
                var regexPattern = ConvertGlobToRegex(normalizedPattern, isRelative: false);
                FileNameRegex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            var isRelative = !normalizedPattern.StartsWith('/') && !normalizedPattern.StartsWith('*');
            var fullRegexPattern = ConvertGlobToRegex(normalizedPattern, isRelative);
            Regex = new System.Text.RegularExpressions.Regex(fullRegexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public bool Matches(string normalizedPath, string fileName)
        {
            if (FileNameRegex is not null && !string.IsNullOrEmpty(fileName))
            {
                if (FileNameRegex.IsMatch(fileName))
                {
                    return true;
                }
            }
            return Regex.IsMatch(normalizedPath);
        }
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
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
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

        var compiledIncludes = includePatterns?.Select(p => new CompiledPattern(p)).ToList();
        var compiledExcludes = excludePatterns?.Select(p => new CompiledPattern(p)).ToList();

        var survivingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                var normalizedPath = entry.RelativePath.Replace('\\', '/');
                var fileName = Path.GetFileName(normalizedPath);

                bool isIncluded = true;
                if (compiledIncludes is { Count: > 0 })
                {
                    isIncluded = compiledIncludes.Any(p => p.Matches(normalizedPath, fileName));
                }

                if (isIncluded && compiledExcludes is { Count: > 0 })
                {
                    if (compiledExcludes.Any(p => p.Matches(normalizedPath, fileName)))
                    {
                        isIncluded = false;
                    }
                }

                if (isIncluded)
                {
                    survivingFiles.Add(entry.RelativePath);
                }
            }
        }

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
