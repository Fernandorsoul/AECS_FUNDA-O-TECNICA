using System.Text.RegularExpressions;

namespace AECS.Application.SemanticLinter;

public class BreakingChangeRule
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public BreakingChangeType Type { get; init; }
    public RuleSeverity Severity { get; init; } = RuleSeverity.Warning;
}

public enum BreakingChangeType
{
    RemovedPublicMethod,
    RemovedPublicProperty,
    ChangedMethodSignature,
    RemovedInterface,
    ChangedReturnType,
    RemovedEnumValue,
    ChangedVisibility,
    RemovedClass
}

public class BreakingChange
{
    public string RuleId { get; init; } = string.Empty;
    public string RuleName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public RuleSeverity Severity { get; init; }
    public BreakingChangeType Type { get; init; }
    public string SymbolName { get; init; } = string.Empty;
}

public class EB003Result
{
    public bool HasBreakingChanges => BreakingChanges.Count > 0;
    public List<BreakingChange> BreakingChanges { get; init; } = [];
    public int FilesAnalyzed { get; init; }
}

public class EB003BreakingChangeVerifier
{
    private static readonly Regex PublicMethodRegex = new(
        @"(?:public)\s+(?:static\s+)?(?:virtual\s+)?(?:override\s+)?(?:async\s+)?[\w<>\[\]?,\s]+\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex PublicPropertyRegex = new(
        @"(?:public)\s+(?:static\s+)?(?:virtual\s+)?(?:override\s+)?[\w<>\[\]?]+\s+(\w+)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex PublicClassRegex = new(
        @"(?:public)\s+(?:static\s+)?(?:partial\s+)?(?:abstract\s+)?(?:class|interface|enum|struct|record)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex EnumValueRegex = new(
        @"^\s*(\w+)\s*[=,]",
        RegexOptions.Compiled);

    public EB003Result Verify(string repoPath, string diffContent)
    {
        var breakingChanges = new List<BreakingChange>();
        var filesAnalyzed = 0;

        // Parse diff to find removed/changed lines
        var removedLines = ParseRemovedLines(diffContent);
        var addedLines = ParseAddedLines(diffContent);

        // Check for removed public methods
        foreach (var (file, lines) in removedLines)
        {
            filesAnalyzed++;
            var relativePath = file.Replace('\\', '/');

            foreach (var line in lines)
            {
                // Removed public method
                var methodMatch = PublicMethodRegex.Match(line);
                if (methodMatch.Success)
                {
                    var methodName = methodMatch.Groups[1].Value;
                    // Check if it was added back (moved, not removed)
                    if (!IsMethodAddedBack(addedLines, file, methodName))
                    {
                        breakingChanges.Add(new BreakingChange
                        {
                            RuleId = "EB003-REMOVED-METHOD",
                            RuleName = "Removed Public Method",
                            FilePath = relativePath,
                            Detail = $"Public method '{methodName}' was removed. This may break consumers.",
                            Severity = RuleSeverity.Error,
                            Type = BreakingChangeType.RemovedPublicMethod,
                            SymbolName = methodName
                        });
                    }
                }

                // Removed public property
                var propMatch = PublicPropertyRegex.Match(line);
                if (propMatch.Success)
                {
                    var propName = propMatch.Groups[1].Value;
                    if (!IsPropertyAddedBack(addedLines, file, propName))
                    {
                        breakingChanges.Add(new BreakingChange
                        {
                            RuleId = "EB003-REMOVED-PROPERTY",
                            RuleName = "Removed Public Property",
                            FilePath = relativePath,
                            Detail = $"Public property '{propName}' was removed. This may break consumers.",
                            Severity = RuleSeverity.Error,
                            Type = BreakingChangeType.RemovedPublicProperty,
                            SymbolName = propName
                        });
                    }
                }

                // Removed class/interface
                var classMatch = PublicClassRegex.Match(line);
                if (classMatch.Success)
                {
                    var className = classMatch.Groups[1].Value;
                    if (!IsClassAddedBack(addedLines, file, className))
                    {
                        breakingChanges.Add(new BreakingChange
                        {
                            RuleId = "EB003-REMOVED-CLASS",
                            RuleName = "Removed Public Class/Interface",
                            FilePath = relativePath,
                            Detail = $"Public type '{className}' was removed. This is a breaking change.",
                            Severity = RuleSeverity.Critical,
                            Type = BreakingChangeType.RemovedClass,
                            SymbolName = className
                        });
                    }
                }
            }
        }

        // Check for method signature changes
        CheckMethodSignatureChanges(removedLines, addedLines, breakingChanges);

        return new EB003Result
        {
            BreakingChanges = breakingChanges,
            FilesAnalyzed = filesAnalyzed
        };
    }

    private static void CheckMethodSignatureChanges(
        Dictionary<string, List<string>> removedLines,
        Dictionary<string, List<string>> addedLines,
        List<BreakingChange> breakingChanges)
    {
        foreach (var (file, removed) in removedLines)
        {
            if (!addedLines.TryGetValue(file, out var added))
                continue;

            var removedMethods = removed
                .Select(l => PublicMethodRegex.Match(l))
                .Where(m => m.Success)
                .ToDictionary(m => m.Groups[1].Value, m => m.Value);

            var addedMethods = added
                .Select(l => PublicMethodRegex.Match(l))
                .Where(m => m.Success)
                .ToDictionary(m => m.Groups[1].Value, m => m.Value);

            // Find methods that exist in both but with different signatures
            foreach (var (methodName, removedSig) in removedMethods)
            {
                if (addedMethods.TryGetValue(methodName, out var addedSig))
                {
                    if (removedSig != addedSig)
                    {
                        breakingChanges.Add(new BreakingChange
                        {
                            RuleId = "EB003-CHANGED-SIGNATURE",
                            RuleName = "Changed Method Signature",
                            FilePath = file.Replace('\\', '/'),
                            Detail = $"Method '{methodName}' signature changed from '{removedSig.Trim()}' to '{addedSig.Trim()}'",
                            Severity = RuleSeverity.Error,
                            Type = BreakingChangeType.ChangedMethodSignature,
                            SymbolName = methodName
                        });
                    }
                }
            }
        }
    }

    private static Dictionary<string, List<string>> ParseRemovedLines(string diff)
    {
        var result = new Dictionary<string, List<string>>();
        var currentFile = "";

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- a/"))
            {
                currentFile = line[6..].Trim();
                if (!result.ContainsKey(currentFile))
                    result[currentFile] = [];
            }
            else if (line.StartsWith("-") && !line.StartsWith("---") && !string.IsNullOrEmpty(currentFile))
            {
                result[currentFile].Add(line[1..]);
            }
        }

        return result;
    }

    private static Dictionary<string, List<string>> ParseAddedLines(string diff)
    {
        var result = new Dictionary<string, List<string>>();
        var currentFile = "";

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ b/"))
            {
                currentFile = line[6..].Trim();
                if (!result.ContainsKey(currentFile))
                    result[currentFile] = [];
            }
            else if (line.StartsWith("+") && !line.StartsWith("+++") && !string.IsNullOrEmpty(currentFile))
            {
                result[currentFile].Add(line[1..]);
            }
        }

        return result;
    }

    private static bool IsMethodAddedBack(Dictionary<string, List<string>> addedLines, string file, string methodName)
    {
        if (!addedLines.TryGetValue(file, out var lines))
            return false;

        return lines.Any(l =>
        {
            var match = PublicMethodRegex.Match(l);
            return match.Success && match.Groups[1].Value == methodName;
        });
    }

    private static bool IsPropertyAddedBack(Dictionary<string, List<string>> addedLines, string file, string propName)
    {
        if (!addedLines.TryGetValue(file, out var lines))
            return false;

        return lines.Any(l =>
        {
            var match = PublicPropertyRegex.Match(l);
            return match.Success && match.Groups[1].Value == propName;
        });
    }

    private static bool IsClassAddedBack(Dictionary<string, List<string>> addedLines, string file, string className)
    {
        if (!addedLines.TryGetValue(file, out var lines))
            return false;

        return lines.Any(l =>
        {
            var match = PublicClassRegex.Match(l);
            return match.Success && match.Groups[1].Value == className;
        });
    }
}
