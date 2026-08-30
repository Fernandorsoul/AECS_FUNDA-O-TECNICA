using System.Text;
using System.Text.RegularExpressions;

namespace AECS.Application.ContextCompiler;

internal static class PathScopeMatcher
{
    public static bool MatchesAny(string relativePath, IEnumerable<string> patterns)
    {
        var normalizedPath = NormalizePath(relativePath);
        return patterns.Any(pattern => Matches(normalizedPath, pattern));
    }

    public static bool Matches(string relativePath, string pattern)
    {
        var normalizedPath = NormalizePath(relativePath);
        var normalizedPattern = NormalizePattern(pattern);
        var regex = new StringBuilder("^");

        for (var index = 0; index < normalizedPattern.Length; index++)
        {
            var current = normalizedPattern[index];
            if (current == '*')
            {
                var isDoubleWildcard = index + 1 < normalizedPattern.Length &&
                    normalizedPattern[index + 1] == '*';
                if (isDoubleWildcard)
                {
                    regex.Append(".*");
                    index++;
                }
                else
                {
                    regex.Append("[^/]*");
                }

                continue;
            }

            regex.Append(Regex.Escape(current.ToString()));
        }

        regex.Append('$');
        return Regex.IsMatch(
            normalizedPath,
            regex.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string NormalizePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new InvalidOperationException("Scope patterns cannot be empty.");
        if (Path.IsPathRooted(pattern))
            throw new InvalidOperationException($"Scope pattern must be repository-relative: {pattern}");

        var normalized = pattern.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment == ".."))
            throw new InvalidOperationException($"Scope pattern cannot traverse directories: {pattern}");

        return normalized.StartsWith("./", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }
}
