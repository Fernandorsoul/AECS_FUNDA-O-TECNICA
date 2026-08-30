using System.Text.RegularExpressions;

namespace AECS.Application.SemanticLinter;

public class DecisionConflict
{
    public string RuleId { get; init; } = string.Empty;
    public string RuleName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public RuleSeverity Severity { get; init; }
    public string ConflictingDecision { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty; // ADR, incident, etc.
}

public class EB005Result
{
    public bool HasConflicts => Conflicts.Count > 0;
    public List<DecisionConflict> Conflicts { get; init; } = [];
    public int FilesAnalyzed { get; init; }
    public int DecisionsChecked { get; init; }
}

public class EB005HistoricalConflictVerifier
{
    private static readonly Regex UsingRegex = new(
        @"using\s+([\w.]+);",
        RegexOptions.Compiled);

    public EB005Result Verify(string repoPath, List<HistoricalDecision> decisions)
    {
        var conflicts = new List<DecisionConflict>();
        var filesAnalyzed = 0;

        var csFiles = Directory.GetFiles(repoPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) &&
                        !f.Contains(Path.Combine("bin", "")))
            .ToList();

        foreach (var file in csFiles)
        {
            filesAnalyzed++;
            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');

            var usings = UsingRegex.Matches(content)
                .Select(m => m.Groups[1].Value)
                .ToList();

            foreach (var decision in decisions)
            {
                // Check if code violates a historical decision
                if (decision.Type == DecisionType.Adr &&
                    decision.ProhibitedPatterns.Any(pattern =>
                        usings.Any(u => u.Contains(pattern, StringComparison.OrdinalIgnoreCase)) ||
                        content.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    conflicts.Add(new DecisionConflict
                    {
                        RuleId = "EB005-ADR-CONFLICT",
                        RuleName = "ADR Conflict",
                        FilePath = relativePath,
                        Detail = $"File '{relativePath}' may conflict with {decision.Source}: {decision.Description}",
                        Severity = RuleSeverity.Warning,
                        ConflictingDecision = decision.Description,
                        Source = decision.Source
                    });
                }

                // Check for incident-related patterns
                if (decision.Type == DecisionType.Incident &&
                    decision.ProhibitedPatterns.Any(pattern =>
                        content.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    conflicts.Add(new DecisionConflict
                    {
                        RuleId = "EB005-INCIDENT-CONFLICT",
                        RuleName = "Incident Pattern",
                        FilePath = relativePath,
                        Detail = $"File '{relativePath}' uses pattern related to incident {decision.Source}: {decision.Description}",
                        Severity = RuleSeverity.Error,
                        ConflictingDecision = decision.Description,
                        Source = decision.Source
                    });
                }
            }
        }

        return new EB005Result
        {
            Conflicts = conflicts,
            FilesAnalyzed = filesAnalyzed,
            DecisionsChecked = decisions.Count
        };
    }
}

public class HistoricalDecision
{
    public string Id { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty; // ADR-001, INC-2024-001, etc.
    public DecisionType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public List<string> ProhibitedPatterns { get; init; } = [];
    public List<string> RequiredPatterns { get; init; } = [];
}

public enum DecisionType
{
    Adr,
    Incident,
    Decision,
    Policy
}
