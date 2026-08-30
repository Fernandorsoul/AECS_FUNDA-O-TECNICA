using System.Text.RegularExpressions;

namespace AECS.Application.SemanticLinter;

public class EB002PatternVerifier
{
    private readonly List<PatternRule> _rules;

    public EB002PatternVerifier(List<PatternRule>? rules = null)
    {
        _rules = rules ?? DefaultPatternRules.GetCommonPatternRules();
    }

    public EB002Result Verify(string repoPath)
    {
        var violations = new List<PatternViolation>();
        var filesScanned = 0;
        var patternStats = new Dictionary<string, int>();

        var csFiles = Directory.GetFiles(repoPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) &&
                        !f.Contains(Path.Combine("bin", "")) &&
                        !f.Contains(Path.Combine("tests", "")))
            .ToList();

        // First pass: collect all class/interface names for pattern analysis
        var allClasses = new Dictionary<string, List<string>>(); // namespace -> class names
        var allInterfaces = new HashSet<string>();

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');

            // Extract namespace
            var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)");
            var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "";

            // Extract class names
            var classMatches = Regex.Matches(content, @"(?:public|internal)\s+(?:static\s+)?(?:partial\s+)?(?:class|interface|enum|struct|record)\s+(\w+)");
            foreach (Match match in classMatches)
            {
                var name = match.Groups[1].Value;
                if (match.Value.Contains("interface"))
                    allInterfaces.Add(name);
                else
                {
                    if (!allClasses.ContainsKey(ns))
                        allClasses[ns] = [];
                    allClasses[ns].Add(name);
                }
            }
        }

        // Second pass: check rules
        foreach (var file in csFiles)
        {
            filesScanned++;
            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');

            var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)");
            var fileNamespace = nsMatch.Success ? nsMatch.Groups[1].Value : "";

            foreach (var rule in _rules)
            {
                // Check scope
                if (!string.IsNullOrEmpty(rule.Scope) &&
                    !fileNamespace.Contains(rule.Scope, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (rule.Type)
                {
                    case PatternType.InterfaceImplementation:
                        CheckInterfaceImplementation(content, fileNamespace, rule, relativePath, allInterfaces, violations, patternStats);
                        break;

                    case PatternType.MethodSignature:
                        CheckMethodSignature(content, rule, relativePath, violations, patternStats);
                        break;

                    case PatternType.NamingConvention:
                        CheckNamingConvention(content, rule, relativePath, violations, patternStats);
                        break;
                }
            }
        }

        return new EB002Result
        {
            Violations = violations,
            FilesScanned = filesScanned,
            RulesChecked = _rules.Count,
            PatternStats = patternStats
        };
    }

    private static void CheckInterfaceImplementation(
        string content, string fileNamespace, PatternRule rule,
        string filePath, HashSet<string> allInterfaces,
        List<PatternViolation> violations, Dictionary<string, int> stats)
    {
        var classMatches = Regex.Matches(content, rule.Pattern);
        foreach (Match match in classMatches)
        {
            var className = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;

            // Check if corresponding interface exists
            var expectedInterface = $"I{className}";
            if (!allInterfaces.Contains(expectedInterface))
            {
                violations.Add(new PatternViolation
                {
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    FilePath = filePath,
                    ViolationDetail = $"Class '{className}' does not implement interface '{expectedInterface}'. {rule.Description}",
                    Severity = rule.Severity,
                    ExpectedPattern = expectedInterface,
                    ActualPattern = className
                });

                if (!stats.ContainsKey(rule.Id)) stats[rule.Id] = 0;
                stats[rule.Id]++;
            }
        }
    }

    private static void CheckMethodSignature(
        string content, PatternRule rule,
        string filePath, List<PatternViolation> violations, Dictionary<string, int> stats)
    {
        var matches = Regex.Matches(content, rule.Pattern);
        foreach (Match match in matches)
        {
            violations.Add(new PatternViolation
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                FilePath = filePath,
                ViolationDetail = $"Found direct instantiation in controller: '{match.Value}'. {rule.Description}",
                Severity = rule.Severity,
                ExpectedPattern = rule.ExpectedPattern,
                ActualPattern = match.Value
            });

            if (!stats.ContainsKey(rule.Id)) stats[rule.Id] = 0;
            stats[rule.Id]++;
        }
    }

    private static void CheckNamingConvention(
        string content, PatternRule rule,
        string filePath, List<PatternViolation> violations, Dictionary<string, int> stats)
    {
        if (rule.Id == "EB002-ASYNC-NAMING")
        {
            // Find async methods that don't end with Async
            var asyncMethodPattern = @"(?:public|private|protected|internal)\s+async\s+Task\S*\s+(\w+)\s*\(";
            var matches = Regex.Matches(content, asyncMethodPattern);
            foreach (Match match in matches)
            {
                var methodName = match.Groups[1].Value;
                if (!methodName.EndsWith("Async"))
                {
                    violations.Add(new PatternViolation
                    {
                        RuleId = rule.Id,
                        RuleName = rule.Name,
                        FilePath = filePath,
                        ViolationDetail = $"Async method '{methodName}' should end with 'Async' suffix",
                        Severity = rule.Severity,
                        ExpectedPattern = $"{methodName}Async",
                        ActualPattern = methodName
                    });

                    if (!stats.ContainsKey(rule.Id)) stats[rule.Id] = 0;
                    stats[rule.Id]++;
                }
            }
        }
    }
}
