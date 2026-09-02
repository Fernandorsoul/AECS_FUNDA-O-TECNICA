using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public sealed class DecisionConflict
{
    public string RuleId { get; init; } = string.Empty;
    public HistoricalDecision Decision { get; init; } = new();
    public HistoricalDecisionPattern Pattern { get; init; } = new();
    public string SymbolId { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public RuleSeverity Severity { get; init; }
    public HistoricalDecisionSuppression? Suppression { get; init; }
    public bool Suppressed => Suppression is not null;
}

public sealed class EB005Result
{
    public HistoricalDecisionSelection Selection { get; init; } = new();
    public List<DecisionConflict> Conflicts { get; init; } = [];
    public List<DecisionConflict> SuppressedConflicts { get; init; } = [];
    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasBlockingConflicts => Conflicts.Any(conflict =>
        conflict.Decision.Enforcement == HistoricalDecisionEnforcement.Blocking);
    public int SymbolsAnalyzed { get; init; }
    public int DecisionsChecked { get; init; }
}

public sealed class EB005HistoricalConflictVerifier
{
    public EB005Result Verify(
        SemanticAnalysisInput input,
        HistoricalDecisionSelection selection)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Status != HistoricalDecisionSelectionStatus.Selected)
            return new EB005Result { Selection = selection };

        var all = new List<DecisionConflict>();
        foreach (var decision in selection.Decisions)
        {
            var relevantNodes = HistoricalDecisionSelector.RelevantNodes(input, decision)
                .OrderBy(node => node.Id, StringComparer.Ordinal)
                .ToList();
            foreach (var pattern in decision.ProhibitedPatterns)
            {
                foreach (var symbol in Matches(input, relevantNodes, pattern))
                    all.Add(Conflict(decision, pattern, symbol, required: false));
            }

            foreach (var pattern in decision.RequiredPatterns)
            {
                if (Matches(input, relevantNodes, pattern).Any())
                    continue;
                var anchor = relevantNodes.First();
                all.Add(Conflict(decision, pattern, anchor, required: true));
            }
        }

        var resolved = all
            .DistinctBy(conflict =>
                $"{conflict.Decision.Id}|{conflict.Decision.Version}|" +
                $"{conflict.Pattern.Kind}|{conflict.Pattern.Value}|{conflict.SymbolId}",
                StringComparer.Ordinal)
            .Select(conflict => ApplySuppression(conflict, selection.Suppressions))
            .OrderBy(conflict => conflict.Decision.Id, StringComparer.Ordinal)
            .ThenBy(conflict => conflict.FilePath, StringComparer.Ordinal)
            .ThenBy(conflict => conflict.SymbolId, StringComparer.Ordinal)
            .ToList();
        return new EB005Result
        {
            Selection = selection,
            Conflicts = resolved.Where(conflict => !conflict.Suppressed).ToList(),
            SuppressedConflicts = resolved.Where(conflict => conflict.Suppressed).ToList(),
            SymbolsAnalyzed = selection.Decisions
                .SelectMany(decision => HistoricalDecisionSelector.RelevantNodes(input, decision))
                .Select(node => node.Id)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            DecisionsChecked = selection.Decisions.Count
        };
    }

    private static IEnumerable<CSharpSymbolGraphNode> Matches(
        SemanticAnalysisInput input,
        IReadOnlyCollection<CSharpSymbolGraphNode> relevantNodes,
        HistoricalDecisionPattern pattern)
    {
        if (pattern.Kind is HistoricalPatternKind.SymbolName or
            HistoricalPatternKind.TypeName or HistoricalPatternKind.NamespacePrefix)
        {
            return relevantNodes.Where(node => MatchesNode(input, node, pattern));
        }

        var edgeKind = pattern.Kind switch
        {
            HistoricalPatternKind.ConstructsType => "constructs",
            HistoricalPatternKind.ReferencesSymbol => "references",
            HistoricalPatternKind.ImplementsType => "implements",
            HistoricalPatternKind.InheritsType => "inherits",
            HistoricalPatternKind.ProjectReference => "project-reference",
            _ => string.Empty
        };
        var relevantIds = relevantNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        return input.CandidateGraph.Edges
            .Where(edge => edge.Kind == edgeKind && relevantIds.Contains(edge.FromNodeId))
            .Where(edge => input.CandidateNode(edge.ToNodeId) is { } target &&
                MatchesTarget(input, target, pattern.Value))
            .Select(edge => input.CandidateNode(edge.FromNodeId)!)
            .DistinctBy(node => node.Id, StringComparer.Ordinal);
    }

    private static bool MatchesNode(
        SemanticAnalysisInput input,
        CSharpSymbolGraphNode node,
        HistoricalDecisionPattern pattern) => pattern.Kind switch
        {
            HistoricalPatternKind.SymbolName =>
                node.Name.Equals(pattern.Value, StringComparison.Ordinal),
            HistoricalPatternKind.TypeName =>
                node.Kind == "type" && node.Name.Equals(pattern.Value, StringComparison.Ordinal),
            HistoricalPatternKind.NamespacePrefix => input.NamespaceOf(
                    node,
                    input.CandidateGraph)
                .Equals(pattern.Value, StringComparison.Ordinal) || input.NamespaceOf(
                    node,
                    input.CandidateGraph)
                .StartsWith(pattern.Value + ".", StringComparison.Ordinal),
            _ => false
        };

    private static bool MatchesTarget(
        SemanticAnalysisInput input,
        CSharpSymbolGraphNode target,
        string expected)
    {
        var targetNamespace = input.NamespaceOf(target, input.CandidateGraph);
        return target.Name.Equals(expected, StringComparison.Ordinal) ||
            target.DisplayName.Equals(expected, StringComparison.Ordinal) ||
            target.DocumentationId.Equals(expected, StringComparison.Ordinal) ||
            target.ProjectPath.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            targetNamespace.Equals(expected, StringComparison.Ordinal) ||
            targetNamespace.StartsWith(expected + ".", StringComparison.Ordinal);
    }

    private static DecisionConflict Conflict(
        HistoricalDecision decision,
        HistoricalDecisionPattern pattern,
        CSharpSymbolGraphNode symbol,
        bool required)
    {
        var ruleId = $"EB005-{decision.Type.ToString().ToUpperInvariant()}-CONFLICT";
        var expectation = required ? "requires" : "prohibits";
        return new DecisionConflict
        {
            RuleId = ruleId,
            Decision = decision,
            Pattern = pattern,
            SymbolId = symbol.Id,
            Symbol = symbol.DisplayName,
            FilePath = SemanticAnalysisInput.Location(symbol),
            Severity = decision.Enforcement == HistoricalDecisionEnforcement.Blocking
                ? RuleSeverity.Error
                : RuleSeverity.Warning,
            Detail = $"{decision.Type} '{decision.Id}' v{decision.Version} from " +
                $"'{decision.Source}'@'{decision.SourceVersion}' {expectation} semantic pattern " +
                $"'{pattern.Kind}:{pattern.Value}' for '{symbol.DisplayName}': " +
                decision.Justification
        };
    }

    private static DecisionConflict ApplySuppression(
        DecisionConflict conflict,
        IEnumerable<HistoricalDecisionSuppression> suppressions)
    {
        var suppression = suppressions.FirstOrDefault(item =>
            item.DecisionId.Equals(conflict.Decision.Id, StringComparison.Ordinal) &&
            item.DecisionVersion == conflict.Decision.Version &&
            (string.IsNullOrEmpty(item.SymbolId) ||
             item.SymbolId.Equals(conflict.SymbolId, StringComparison.Ordinal)) &&
            (string.IsNullOrEmpty(item.FilePath) ||
             item.FilePath.Equals(conflict.FilePath, StringComparison.OrdinalIgnoreCase)));
        if (suppression is null)
            return conflict;
        return new DecisionConflict
        {
            RuleId = conflict.RuleId,
            Decision = conflict.Decision,
            Pattern = conflict.Pattern,
            SymbolId = conflict.SymbolId,
            Symbol = conflict.Symbol,
            FilePath = conflict.FilePath,
            Detail = conflict.Detail,
            Severity = conflict.Severity,
            Suppression = suppression
        };
    }
}
