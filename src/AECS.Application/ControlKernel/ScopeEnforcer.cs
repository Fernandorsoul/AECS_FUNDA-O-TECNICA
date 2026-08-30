using AECS.Domain.Models;

namespace AECS.Application.ControlKernel;

public class ScopeViolation
{
    public string FilePath { get; init; } = string.Empty;
    public string ViolationType { get; init; } = string.Empty; // "ForbiddenFile" or "NotAllowed"
    public string Message { get; init; } = string.Empty;
}

public class ScopeEnforcer
{
    public IReadOnlyList<ScopeViolation> Check(ScopeDefinition scope, IReadOnlyList<string> changedFiles)
    {
        var violations = new List<ScopeViolation>();

        foreach (var file in changedFiles)
        {
            // Check forbidden first
            if (scope.Forbidden.Any(pattern => MatchesGlob(file, pattern)))
            {
                violations.Add(new ScopeViolation
                {
                    FilePath = file,
                    ViolationType = "ForbiddenFile",
                    Message = $"File '{file}' matches forbidden pattern"
                });
                continue;
            }

            // Check allowed (if allowed list is not empty, file must match at least one)
            if (scope.Allowed.Count > 0 && !scope.Allowed.Any(pattern => MatchesGlob(file, pattern)))
            {
                violations.Add(new ScopeViolation
                {
                    FilePath = file,
                    ViolationType = "NotAllowed",
                    Message = $"File '{file}' does not match any allowed pattern"
                });
            }
        }

        return violations;
    }

    public static bool MatchesGlob(string filePath, string pattern)
    {
        // Normalize separators
        filePath = filePath.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');

        // Handle ** wildcard (matches any path segment)
        if (pattern.EndsWith("/**"))
        {
            var prefix = pattern[..^3]; // Remove /**
            return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Handle * wildcard (matches within a segment)
        if (pattern.Contains('*'))
        {
            var regexPattern = "^" + pattern
                .Replace(".", "\\.")
                .Replace("**", ".*")
                .Replace("*", "[^/]*")
                + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(filePath, regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Exact match
        return string.Equals(filePath, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
