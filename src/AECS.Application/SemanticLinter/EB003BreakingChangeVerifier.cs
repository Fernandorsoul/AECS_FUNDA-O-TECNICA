using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

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

public sealed class EB003Result
{
    public bool HasBreakingChanges => BreakingChanges.Count > 0;
    public List<SemanticRuleFinding> BreakingChanges { get; init; } = [];
    public int FilesAnalyzed { get; init; }
}

public sealed class EB003BreakingChangeVerifier
{
    public EB003Result Verify(SemanticAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var candidateApi = input.CandidateGraph.Nodes
            .Where(node => input.IsPublicApi(node, input.CandidateGraph))
            .GroupBy(ApiIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(node => node.Id, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);
        var findings = new List<SemanticRuleFinding>();
        foreach (var baseline in input.ImpactedBaselineNodes()
                     .Where(node => input.IsPublicApi(node, input.BaselineGraph))
                     .OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            var identity = ApiIdentity(baseline);
            if (!candidateApi.TryGetValue(identity, out var candidate))
            {
                findings.Add(Removed(input, baseline));
                continue;
            }
            var baselineSignature = ApiSignature(baseline);
            var candidateSignature = ApiSignature(candidate);
            if (!string.Equals(baselineSignature, candidateSignature, StringComparison.Ordinal))
            {
                findings.Add(new SemanticRuleFinding
                {
                    RuleId = "EB003-CHANGED-SIGNATURE",
                    RuleName = "Changed Public API Signature",
                    SymbolId = candidate.Id,
                    Symbol = candidate.DisplayName,
                    FilePath = SemanticAnalysisInput.Location(candidate),
                    Severity = RuleSeverity.Critical,
                    Baseline = input.BaselineSnapshot.BaselineCommit,
                    Category = BreakingChangeType.ChangedMethodSignature.ToString(),
                    Justification =
                        $"Public API changed semantically from '{baselineSignature}' " +
                        $"to '{candidateSignature}'."
                });
            }
        }
        return new EB003Result
        {
            BreakingChanges = findings
                .DistinctBy(finding => $"{finding.RuleId}|{finding.SymbolId}|{finding.Justification}")
                .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.FilePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.SymbolId, StringComparer.Ordinal)
                .ToList(),
            FilesAnalyzed = input.ChangedFiles.Count(path => path.EndsWith(
                ".cs",
                StringComparison.OrdinalIgnoreCase))
        };
    }

    private static SemanticRuleFinding Removed(
        SemanticAnalysisInput input,
        CSharpSymbolGraphNode node)
    {
        var isType = node.Kind == "type";
        var changeType = isType
            ? node.TypeKind == "Interface"
                ? BreakingChangeType.RemovedInterface
                : BreakingChangeType.RemovedClass
            : node.MemberKind switch
            {
                "Method" => BreakingChangeType.RemovedPublicMethod,
                "Property" => BreakingChangeType.RemovedPublicProperty,
                _ => BreakingChangeType.ChangedVisibility
            };
        return new SemanticRuleFinding
        {
            RuleId = isType ? "EB003-REMOVED-TYPE" : "EB003-REMOVED-MEMBER",
            RuleName = isType ? "Removed Public Type" : "Removed Public Member",
            SymbolId = node.Id,
            Symbol = node.DisplayName,
            FilePath = SemanticAnalysisInput.Location(node),
            Severity = RuleSeverity.Critical,
            Baseline = input.BaselineSnapshot.BaselineCommit,
            Category = changeType.ToString(),
            Justification =
                $"Public API '{ApiSignature(node)}' from baseline " +
                $"'{input.BaselineSnapshot.BaselineCommit}' has no equivalent resolved symbol " +
                "in the candidate graph."
        };
    }

    private static string ApiIdentity(CSharpSymbolGraphNode node) =>
        string.IsNullOrWhiteSpace(node.DocumentationId)
            ? $"{node.ProjectPath}|{node.Kind}|{node.ContainingNodeId}|{node.Name}|{node.Arity}|{node.MemberKind}"
            : $"{node.ProjectPath}|{node.DocumentationId}";

    private static string ApiSignature(CSharpSymbolGraphNode node)
    {
        var semanticModifiers = node.Modifiers
            .Where(modifier => modifier.StartsWith("constraint:", StringComparison.Ordinal))
            .OrderBy(modifier => modifier, StringComparer.Ordinal);
        return string.Join(
            "|",
            new[]
            {
                node.DisplayName,
                node.Accessibility,
                node.TypeKind,
                node.MemberKind,
                $"arity:{node.Arity}"
            }.Concat(semanticModifiers));
    }
}
