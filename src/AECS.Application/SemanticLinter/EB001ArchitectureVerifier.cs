using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public sealed class EB001Result
{
    public bool HasViolations => Violations.Count > 0;
    public List<SemanticRuleFinding> Violations { get; init; } = [];
    public int FilesScanned { get; init; }
    public int RulesChecked { get; init; }
}

public sealed class EB001ArchitectureVerifier
{
    private static readonly HashSet<string> DependencyEdges = new(
        ["project-reference", "references", "inherits", "implements", "constructs"],
        StringComparer.Ordinal);

    private readonly List<ArchitectureRule> _rules;

    public EB001ArchitectureVerifier(List<ArchitectureRule>? rules = null)
    {
        _rules = rules ?? DefaultArchitectureRules.GetCleanArchitectureRules();
    }

    public EB001Result Verify(SemanticAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var baselineRelations = CollectRelations(input, input.BaselineGraph, impactedOnly: false)
            .Select(relation => relation.Key)
            .ToHashSet(StringComparer.Ordinal);
        var findings = CollectRelations(input, input.CandidateGraph, impactedOnly: true)
            .Where(relation => !baselineRelations.Contains(relation.Key))
            .Select(relation => relation.Finding)
            .DistinctBy(finding =>
                $"{finding.RuleId}\n{finding.SymbolId}\n{finding.Justification}",
                StringComparer.Ordinal)
            .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.FilePath, StringComparer.Ordinal)
            .ThenBy(finding => finding.SymbolId, StringComparer.Ordinal)
            .ToList();
        return new EB001Result
        {
            Violations = findings,
            FilesScanned = input.ImpactedCandidateNodes()
                .SelectMany(node => node.FilePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            RulesChecked = _rules.Count
        };
    }

    private IEnumerable<ArchitectureRelation> CollectRelations(
        SemanticAnalysisInput input,
        CSharpSymbolGraph graph,
        bool impactedOnly)
    {
        foreach (var edge in graph.Edges.Where(edge => DependencyEdges.Contains(edge.Kind)))
        {
            var source = input.Node(graph, edge.FromNodeId);
            var target = input.Node(graph, edge.ToNodeId);
            if (source is null || target is null ||
                (impactedOnly && !input.IsImpacted(source, graph)))
            {
                continue;
            }
            var sourceNamespace = input.NamespaceOf(source, graph);
            foreach (var rule in _rules.Where(rule => MatchesSource(
                         rule.SourceLayer,
                         source,
                         sourceNamespace)))
            {
                foreach (var forbidden in rule.ForbiddenDependencies.Where(value =>
                             MatchesTarget(value, target, input.NamespaceOf(target, graph))))
                {
                    var targetIdentity = Identity(target);
                    var key = $"{rule.Id}|{Identity(source)}|{edge.Kind}|{targetIdentity}|{forbidden}";
                    yield return new ArchitectureRelation(
                        key,
                        new SemanticRuleFinding
                        {
                            RuleId = rule.Id,
                            RuleName = rule.Name,
                            SymbolId = source.Id,
                            Symbol = source.DisplayName,
                            FilePath = SemanticAnalysisInput.Location(source),
                            Severity = rule.Severity,
                            Baseline = input.BaselineSnapshot.BaselineCommit,
                            Category = "dependency",
                            Justification =
                                $"Resolved {edge.Kind} relation from '{source.DisplayName}' " +
                                $"to '{target.DisplayName}' matches forbidden dependency " +
                                $"'{forbidden}': {rule.Description}"
                        });
                }
            }
        }
    }

    private static bool MatchesSource(
        string sourceLayer,
        CSharpSymbolGraphNode source,
        string sourceNamespace) =>
        StartsWith(sourceNamespace, sourceLayer) ||
        StartsWith(source.AssemblyName, sourceLayer) ||
        StartsWith(source.ProjectPath, sourceLayer) ||
        StartsWith(source.DisplayName, sourceLayer);

    private static bool MatchesTarget(
        string forbidden,
        CSharpSymbolGraphNode target,
        string targetNamespace) =>
        StartsWith(targetNamespace, forbidden) ||
        StartsWith(target.AssemblyName, forbidden) ||
        StartsWith(target.ProjectPath, forbidden) ||
        StartsWith(target.DisplayName, forbidden) ||
        target.Name.Equals(forbidden, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string value, string expected) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith(expected, StringComparison.OrdinalIgnoreCase);

    private static string Identity(CSharpSymbolGraphNode node) =>
        string.IsNullOrWhiteSpace(node.DocumentationId)
            ? node.Id
            : $"{node.ProjectPath}|{node.DocumentationId}";

    private sealed record ArchitectureRelation(string Key, SemanticRuleFinding Finding);
}
