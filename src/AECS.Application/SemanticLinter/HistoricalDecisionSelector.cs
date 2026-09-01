using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public static class HistoricalDecisionSelector
{
    public static async Task<HistoricalDecisionSelection> LoadAsync(
        VerificationContext context,
        IHistoricalDecisionStore? store,
        DateTime evaluatedAt,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return Unavailable(
                evaluatedAt,
                "The operational store does not provide historical decisions.");
        }

        try
        {
            var input = SemanticAnalysisInput.From(context);
            var decisions = await store.LoadHistoricalDecisionsAsync(cancellationToken);
            var suppressions = await store.LoadHistoricalDecisionSuppressionsAsync(
                cancellationToken);
            return Select(input, decisions, suppressions, evaluatedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(
                evaluatedAt,
                $"Historical decision selection failed: {ex.Message}");
        }
    }

    public static HistoricalDecisionSelection Select(
        SemanticAnalysisInput input,
        IEnumerable<HistoricalDecision> decisions,
        IEnumerable<HistoricalDecisionSuppression> suppressions,
        DateTime evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(suppressions);

        var versions = decisions.ToList();
        foreach (var decision in versions)
            HistoricalDecisionContract.Validate(decision);
        var latestReviewed = versions
            .GroupBy(decision => decision.Id, StringComparer.Ordinal)
            .Select(group => group
                .Where(decision =>
                    decision.Review.Status != HistoricalDecisionReviewStatus.Draft)
                .OrderByDescending(decision => decision.Version)
                .FirstOrDefault())
            .Where(decision => decision is not null)
            .Select(decision => decision!)
            .Where(decision =>
                decision.Review.Status == HistoricalDecisionReviewStatus.Approved &&
                decision.ValidFrom <= evaluatedAt &&
                (decision.ValidUntil is null || decision.ValidUntil > evaluatedAt))
            .Where(decision => RelevantNodes(input, decision).Any())
            .OrderBy(decision => decision.Id, StringComparer.Ordinal)
            .ThenBy(decision => decision.Version)
            .ToList();
        if (latestReviewed.Count == 0)
        {
            return new HistoricalDecisionSelection
            {
                Status = HistoricalDecisionSelectionStatus.NoHistory,
                EvaluatedAt = evaluatedAt,
                Message = "No approved, valid historical decision is relevant to the candidate."
            };
        }

        var prohibited = latestReviewed.SelectMany(decision =>
            decision.ProhibitedPatterns.Select(PatternKey))
            .ToHashSet(StringComparer.Ordinal);
        var required = latestReviewed.SelectMany(decision =>
            decision.RequiredPatterns.Select(PatternKey))
            .ToHashSet(StringComparer.Ordinal);
        var ambiguous = prohibited.Intersect(required, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (ambiguous.Count > 0)
        {
            return new HistoricalDecisionSelection
            {
                Status = HistoricalDecisionSelectionStatus.Ambiguous,
                EvaluatedAt = evaluatedAt,
                Message = "Active historical decisions both require and prohibit: " +
                    string.Join(", ", ambiguous),
                Decisions = latestReviewed
            };
        }

        var decisionKeys = latestReviewed
            .Select(decision => $"{decision.Id}\n{decision.Version}")
            .ToHashSet(StringComparer.Ordinal);
        var activeSuppressions = suppressions.ToList();
        foreach (var suppression in activeSuppressions)
            HistoricalDecisionContract.Validate(suppression);
        activeSuppressions = activeSuppressions
            .GroupBy(suppression => suppression.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Version).First())
            .Where(suppression => suppression.ExpiresAt > evaluatedAt)
            .Where(suppression => decisionKeys.Contains(
                $"{suppression.DecisionId}\n{suppression.DecisionVersion}"))
            .OrderBy(suppression => suppression.Id, StringComparer.Ordinal)
            .ThenBy(suppression => suppression.Version)
            .ToList();

        return new HistoricalDecisionSelection
        {
            Status = HistoricalDecisionSelectionStatus.Selected,
            EvaluatedAt = evaluatedAt,
            Message = $"Selected {latestReviewed.Count} approved historical decisions.",
            Decisions = latestReviewed,
            Suppressions = activeSuppressions
        };
    }

    public static IEnumerable<CSharpSymbolGraphNode> RelevantNodes(
        SemanticAnalysisInput input,
        HistoricalDecision decision) => input.ImpactedCandidateNodes().Where(node =>
            (decision.Scope.ProjectPaths.Count == 0 ||
             decision.Scope.ProjectPaths.Contains(node.ProjectPath, StringComparer.OrdinalIgnoreCase)) &&
            (decision.Scope.NamespacePrefixes.Count == 0 ||
             decision.Scope.NamespacePrefixes.Any(prefix => input.NamespaceOf(
                     node,
                     input.CandidateGraph)
                 .Equals(prefix, StringComparison.Ordinal) || input.NamespaceOf(
                     node,
                     input.CandidateGraph)
                 .StartsWith(prefix + ".", StringComparison.Ordinal))) &&
            (decision.Scope.SymbolKinds.Count == 0 ||
             decision.Scope.SymbolKinds.Contains(node.Kind, StringComparer.Ordinal)));

    public static HistoricalDecisionSelection FromEvidence(
        HistoricalDecisionVerificationEvidence? evidence,
        DateTime evaluatedAt) => evidence is null
        ? Unavailable(evaluatedAt, "Replay evidence has no historical decision selection.")
        : new HistoricalDecisionSelection
        {
            Status = evidence.Status,
            EvaluatedAt = evidence.EvaluatedAt,
            Message = evidence.Message,
            Decisions = evidence.Decisions,
            Suppressions = evidence.Suppressions
        };

    private static HistoricalDecisionSelection Unavailable(
        DateTime evaluatedAt,
        string message) => new()
        {
            Status = HistoricalDecisionSelectionStatus.Unavailable,
            EvaluatedAt = evaluatedAt,
            Message = message
        };

    private static string PatternKey(HistoricalDecisionPattern pattern) =>
        $"{pattern.Kind}:{pattern.Value.Trim()}";
}
