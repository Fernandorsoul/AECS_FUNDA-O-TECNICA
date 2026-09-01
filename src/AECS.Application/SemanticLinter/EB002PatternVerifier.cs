using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public sealed class EB002Result
{
    public bool HasViolations => Violations.Count > 0;
    public List<SemanticRuleFinding> Violations { get; init; } = [];
    public int FilesScanned { get; init; }
    public int RulesChecked { get; init; }
    public Dictionary<string, int> PatternStats { get; init; } = new();
}

public sealed class EB002PatternVerifier
{
    private readonly List<PatternRule> _rules;

    public EB002PatternVerifier(List<PatternRule>? rules = null)
    {
        _rules = rules ?? DefaultPatternRules.GetCommonPatternRules();
    }

    public EB002Result Verify(SemanticAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var baseline = FindViolations(input, input.BaselineGraph, impactedOnly: false)
            .Select(FindingKey)
            .ToHashSet(StringComparer.Ordinal);
        var findings = FindViolations(input, input.CandidateGraph, impactedOnly: true)
            .Where(finding => !baseline.Contains(FindingKey(finding)))
            .DistinctBy(FindingKey, StringComparer.Ordinal)
            .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.FilePath, StringComparer.Ordinal)
            .ThenBy(finding => finding.SymbolId, StringComparer.Ordinal)
            .ToList();
        return new EB002Result
        {
            Violations = findings,
            FilesScanned = input.ImpactedCandidateNodes()
                .SelectMany(node => node.FilePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            RulesChecked = _rules.Count,
            PatternStats = findings.GroupBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
        };
    }

    private IEnumerable<SemanticRuleFinding> FindViolations(
        SemanticAnalysisInput input,
        CSharpSymbolGraph graph,
        bool impactedOnly)
    {
        foreach (var rule in _rules)
        {
            switch (rule.Type)
            {
                case PatternType.InterfaceImplementation:
                    foreach (var finding in MissingImplementations(input, graph, rule, impactedOnly))
                        yield return finding;
                    break;
                case PatternType.MethodSignature:
                    foreach (var finding in ProhibitedConstructions(input, graph, rule, impactedOnly))
                        yield return finding;
                    break;
                case PatternType.NamingConvention:
                    foreach (var finding in NamingViolations(input, graph, rule, impactedOnly))
                        yield return finding;
                    break;
            }
        }
    }

    private static IEnumerable<SemanticRuleFinding> MissingImplementations(
        SemanticAnalysisInput input,
        CSharpSymbolGraph graph,
        PatternRule rule,
        bool impactedOnly)
    {
        foreach (var type in graph.Nodes.Where(node =>
                     node.Kind == "type" &&
                     node.TypeKind != "Interface" &&
                     MatchesScope(input, graph, node, rule.Scope) &&
                     (string.IsNullOrEmpty(rule.NameSuffix) ||
                      node.Name.EndsWith(rule.NameSuffix, StringComparison.Ordinal)) &&
                     (!impactedOnly || input.IsImpacted(node, graph))))
        {
            var expected = rule.RequiredImplementedType.Replace("{Name}", type.Name, StringComparison.Ordinal);
            var implemented = graph.Edges.Where(edge =>
                    edge.Kind == "implements" && edge.FromNodeId == type.Id)
                .Select(edge => input.Node(graph, edge.ToNodeId))
                .Any(target => target is not null &&
                    (target.Name.Equals(expected, StringComparison.Ordinal) ||
                     target.DisplayName.Contains(expected, StringComparison.Ordinal)));
            if (!implemented)
            {
                yield return Finding(
                    input,
                    rule,
                    type,
                    $"Resolved type '{type.DisplayName}' has no implements edge to " +
                    $"'{expected}'. {rule.Description}");
            }
        }
    }

    private static IEnumerable<SemanticRuleFinding> ProhibitedConstructions(
        SemanticAnalysisInput input,
        CSharpSymbolGraph graph,
        PatternRule rule,
        bool impactedOnly)
    {
        foreach (var edge in graph.Edges.Where(edge => edge.Kind == "constructs"))
        {
            var source = input.Node(graph, edge.FromNodeId);
            var target = input.Node(graph, edge.ToNodeId);
            if (source is null || target is null ||
                !MatchesScope(input, graph, source, rule.Scope) ||
                (impactedOnly && !input.IsImpacted(source, graph)) ||
                !target.Name.EndsWith(rule.ProhibitedConstructedTypeSuffix, StringComparison.Ordinal))
            {
                continue;
            }
            yield return Finding(
                input,
                rule,
                source,
                $"Resolved object creation in '{source.DisplayName}' constructs " +
                $"'{target.DisplayName}'. {rule.Description}");
        }
    }

    private static IEnumerable<SemanticRuleFinding> NamingViolations(
        SemanticAnalysisInput input,
        CSharpSymbolGraph graph,
        PatternRule rule,
        bool impactedOnly)
    {
        foreach (var node in graph.Nodes.Where(node =>
                     (string.IsNullOrEmpty(rule.SymbolKind) || node.Kind == rule.SymbolKind) &&
                     MatchesScope(input, graph, node, rule.Scope) &&
                     (!impactedOnly || input.IsImpacted(node, graph))))
        {
            if (rule.RequireAsyncSuffix &&
                node.MemberKind == "Method" &&
                node.Modifiers.Contains("async", StringComparer.Ordinal) &&
                !node.Name.EndsWith("Async", StringComparison.Ordinal))
            {
                yield return Finding(
                    input,
                    rule,
                    node,
                    $"Resolved async method '{node.DisplayName}' does not end with 'Async'.");
            }
            if (rule.AllowedNameSuffixes.Count > 0 &&
                !rule.AllowedNameSuffixes.Any(suffix =>
                    node.Name.EndsWith(suffix, StringComparison.Ordinal)))
            {
                yield return Finding(
                    input,
                    rule,
                    node,
                    $"Resolved type '{node.DisplayName}' does not use any allowed suffix: " +
                    string.Join(", ", rule.AllowedNameSuffixes));
            }
        }
    }

    private static bool MatchesScope(
        SemanticAnalysisInput input,
        CSharpSymbolGraph graph,
        CSharpSymbolGraphNode node,
        string scope) =>
        string.IsNullOrWhiteSpace(scope) ||
        input.NamespaceOf(node, graph).Contains(scope, StringComparison.OrdinalIgnoreCase) ||
        node.ProjectPath.Contains(scope, StringComparison.OrdinalIgnoreCase) ||
        node.DisplayName.Contains(scope, StringComparison.OrdinalIgnoreCase) ||
        (input.ContainingType(node, graph)?.Name.Contains(
            scope,
            StringComparison.OrdinalIgnoreCase) ?? false);

    private static SemanticRuleFinding Finding(
        SemanticAnalysisInput input,
        PatternRule rule,
        CSharpSymbolGraphNode node,
        string justification) => new()
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            SymbolId = node.Id,
            Symbol = node.DisplayName,
            FilePath = SemanticAnalysisInput.Location(node),
            Severity = rule.Severity,
            Baseline = input.BaselineSnapshot.BaselineCommit,
            Category = rule.Type.ToString(),
            Justification = justification
        };

    private static string FindingKey(SemanticRuleFinding finding) =>
        $"{finding.RuleId}|{finding.Symbol}|{finding.Category}";
}
