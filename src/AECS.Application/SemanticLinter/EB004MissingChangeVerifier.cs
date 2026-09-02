using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public sealed class EB004Result
{
    public bool HasMissingChanges => MissingChanges.Count > 0;
    public List<SemanticRuleFinding> MissingChanges { get; init; } = [];
    public int FilesAnalyzed { get; init; }
}

public sealed class EB004MissingChangeVerifier
{
    public EB004Result Verify(SemanticAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var findings = ContractImplementationFindings(input)
            .Concat(TestRelationshipFindings(input))
            .Concat(ModelMigrationFindings(input))
            .DistinctBy(
                finding => $"{finding.RuleId}|{finding.SymbolId}|{finding.FilePath}",
                StringComparer.Ordinal)
            .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.FilePath, StringComparer.Ordinal)
            .ThenBy(finding => finding.SymbolId, StringComparer.Ordinal)
            .ToList();
        return new EB004Result
        {
            MissingChanges = findings,
            FilesAnalyzed = input.ChangedFiles.Count
        };
    }

    private static IEnumerable<SemanticRuleFinding> ContractImplementationFindings(
        SemanticAnalysisInput input)
    {
        foreach (var graph in new[] { input.BaselineGraph, input.CandidateGraph })
        {
            foreach (var contract in graph.Nodes.Where(node =>
                         node.Kind == "type" &&
                         node.TypeKind == "Interface" &&
                         input.IsImpacted(node, graph)))
            {
                foreach (var edge in graph.Edges.Where(edge =>
                             edge.Kind == "implements" && edge.ToNodeId == contract.Id))
                {
                    var implementation = input.Node(graph, edge.FromNodeId);
                    if (implementation is null ||
                        implementation.FilePaths.Count == 0 ||
                        implementation.FilePaths.Any(input.ChangedFiles.Contains))
                    {
                        continue;
                    }
                    yield return Finding(
                        input,
                        "EB004-MISSING-IMPLEMENTATION",
                        "Missing Related Implementation Change",
                        implementation,
                        RuleSeverity.Error,
                        "contract-implementation",
                        $"Changed contract '{contract.DisplayName}' has a resolved implements " +
                        $"relation from '{implementation.DisplayName}', but none of the " +
                        "implementation declarations changed in the candidate.");
                }
            }
        }
    }

    private static IEnumerable<SemanticRuleFinding> TestRelationshipFindings(
        SemanticAnalysisInput input)
    {
        foreach (var graphAndSnapshot in new[]
                 {
                     (Graph: input.BaselineGraph, Snapshot: input.BaselineSnapshot),
                     (Graph: input.CandidateGraph, Snapshot: input.CandidateSnapshot)
                 })
        {
            var impacted = graphAndSnapshot.Graph.Nodes
                .Where(node => (node.Kind is "type" or "member") &&
                    input.IsImpacted(node, graphAndSnapshot.Graph))
                .Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var edge in graphAndSnapshot.Graph.Edges.Where(edge =>
                         edge.Kind is "references" or "constructs" &&
                         impacted.Contains(edge.ToNodeId)))
            {
                var testSymbol = input.Node(graphAndSnapshot.Graph, edge.FromNodeId);
                var changedSymbol = input.Node(graphAndSnapshot.Graph, edge.ToNodeId);
                if (testSymbol is null || changedSymbol is null ||
                    !input.IsTestProject(testSymbol.ProjectPath, graphAndSnapshot.Snapshot) ||
                    testSymbol.FilePaths.Count == 0 ||
                    testSymbol.FilePaths.Any(input.ChangedFiles.Contains))
                {
                    continue;
                }
                yield return Finding(
                    input,
                    "EB004-MISSING-TEST",
                    "Missing Related Test Change",
                    testSymbol,
                    RuleSeverity.Warning,
                    "test-reference",
                    $"Test symbol '{testSymbol.DisplayName}' has a resolved {edge.Kind} " +
                    $"relation to impacted symbol '{changedSymbol.DisplayName}', but its " +
                    "declaration did not change.");
            }
        }
    }

    private static IEnumerable<SemanticRuleFinding> ModelMigrationFindings(
        SemanticAnalysisInput input)
    {
        var graph = input.CandidateGraph;
        var dbContexts = TypesInheriting(input, graph, "DbContext").ToList();
        var migrations = TypesInheriting(input, graph, "Migration")
            .Concat(TypesInheriting(input, graph, "ModelSnapshot"))
            .DistinctBy(node => node.Id, StringComparer.Ordinal)
            .ToList();
        if (dbContexts.Count == 0 || migrations.Count == 0)
            yield break;

        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var context in dbContexts)
        {
            foreach (var edge in graph.Edges.Where(edge => edge.Kind == "references"))
            {
                var source = input.Node(graph, edge.FromNodeId);
                var target = input.Node(graph, edge.ToNodeId);
                if (source is not null && target is { Kind: "type", IsExternal: false } &&
                    input.IsContainedBy(source, context, graph))
                {
                    entityIds.Add(target.Id);
                }
            }
        }

        foreach (var entity in graph.Nodes.Where(node =>
                     entityIds.Contains(node.Id) && input.IsImpacted(node, graph)))
        {
            var relatedMigrations = migrations.Where(migration =>
                    migration.ProjectPath.Equals(entity.ProjectPath, StringComparison.OrdinalIgnoreCase) ||
                    migration.AssemblyName.Equals(entity.AssemblyName, StringComparison.Ordinal))
                .ToList();
            if (relatedMigrations.Count == 0 || relatedMigrations.Any(migration =>
                    migration.FilePaths.Any(input.ChangedFiles.Contains)))
            {
                continue;
            }
            yield return Finding(
                input,
                "EB004-MISSING-MIGRATION",
                "Missing Related Migration Change",
                entity,
                RuleSeverity.Warning,
                "model-migration",
                $"Impacted entity '{entity.DisplayName}' is referenced by a resolved DbContext " +
                "and its project contains resolved Migration/ModelSnapshot types, but none " +
                "of those declarations changed.");
        }
    }

    private static IEnumerable<CSharpSymbolGraphNode> TypesInheriting(
        SemanticAnalysisInput input,
        CSharpSymbolGraph graph,
        string baseTypeName)
    {
        foreach (var edge in graph.Edges.Where(edge => edge.Kind == "inherits"))
        {
            var source = input.Node(graph, edge.FromNodeId);
            var target = input.Node(graph, edge.ToNodeId);
            if (source is { Kind: "type" } && target is not null &&
                target.Name.Equals(baseTypeName, StringComparison.Ordinal))
            {
                yield return source;
            }
        }
    }

    private static SemanticRuleFinding Finding(
        SemanticAnalysisInput input,
        string ruleId,
        string ruleName,
        CSharpSymbolGraphNode symbol,
        RuleSeverity severity,
        string category,
        string justification) => new()
        {
            RuleId = ruleId,
            RuleName = ruleName,
            SymbolId = symbol.Id,
            Symbol = symbol.DisplayName,
            FilePath = SemanticAnalysisInput.Location(symbol),
            Severity = severity,
            Baseline = input.BaselineSnapshot.BaselineCommit,
            Category = category,
            Justification = justification
        };
}
