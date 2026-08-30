using System.Text.RegularExpressions;

namespace AECS.Application.SemanticLinter;

public class EB001Result
{
    public bool HasViolations => Violations.Count > 0;
    public List<ArchitectureViolation> Violations { get; init; } = [];
    public int FilesScanned { get; init; }
    public int RulesChecked { get; init; }
}

public class EB001ArchitectureVerifier
{
    private static readonly Regex UsingRegex = new(
        @"using\s+([\w.]+);",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceRegex = new(
        @"namespace\s+([\w.]+)",
        RegexOptions.Compiled);

    private readonly List<ArchitectureRule> _rules;

    public EB001ArchitectureVerifier(List<ArchitectureRule>? rules = null)
    {
        _rules = rules ?? DefaultArchitectureRules.GetCleanArchitectureRules();
    }

    public EB001Result Verify(string repoPath)
    {
        var violations = new List<ArchitectureViolation>();
        var filesScanned = 0;

        var csFiles = Directory.GetFiles(repoPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) &&
                        !f.Contains(Path.Combine("bin", "")) &&
                        !f.Contains(Path.Combine("tests", "")))
            .ToList();

        foreach (var file in csFiles)
        {
            filesScanned++;
            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');

            // Get the namespace of this file
            var namespaceMatch = NamespaceRegex.Match(content);
            var fileNamespace = namespaceMatch.Success ? namespaceMatch.Groups[1].Value : "";

            // Get all using statements
            var usings = UsingRegex.Matches(content)
                .Select(m => m.Groups[1].Value)
                .ToList();

            // Check each rule
            foreach (var rule in _rules)
            {
                // Check if this file belongs to the source layer
                if (!BelongsToLayer(fileNamespace, rule.SourceLayer))
                    continue;

                // Check forbidden dependencies
                foreach (var forbidden in rule.ForbiddenDependencies)
                {
                    var violationsFound = usings
                        .Where(u => u.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var violation in violationsFound)
                    {
                        violations.Add(new ArchitectureViolation
                        {
                            RuleId = rule.Id,
                            RuleName = rule.Name,
                            FilePath = relativePath,
                            ViolationDetail = $"File '{relativePath}' (namespace: {fileNamespace}) uses '{violation}' which violates rule: {rule.Description}",
                            Severity = rule.Severity,
                            SourceNamespace = fileNamespace,
                            ForbiddenDependency = violation
                        });
                    }

                    // Also check for direct references in code (not just using statements)
                    if (forbidden.Contains('.') && content.Contains(forbidden))
                    {
                        // Only add if not already caught by using statement check
                        if (!violationsFound.Any())
                        {
                            violations.Add(new ArchitectureViolation
                            {
                                RuleId = rule.Id,
                                RuleName = rule.Name,
                                FilePath = relativePath,
                                ViolationDetail = $"File '{relativePath}' references '{forbidden}' which violates rule: {rule.Description}",
                                Severity = rule.Severity,
                                SourceNamespace = fileNamespace,
                                ForbiddenDependency = forbidden
                            });
                        }
                    }
                }
            }
        }

        return new EB001Result
        {
            Violations = violations,
            FilesScanned = filesScanned,
            RulesChecked = _rules.Count
        };
    }

    private static bool BelongsToLayer(string fileNamespace, string layerPattern)
    {
        return fileNamespace.StartsWith(layerPattern, StringComparison.OrdinalIgnoreCase);
    }
}
