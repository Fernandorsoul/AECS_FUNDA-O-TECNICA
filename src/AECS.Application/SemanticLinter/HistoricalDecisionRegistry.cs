using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public sealed class HistoricalDecisionRegistry
{
    private readonly IHistoricalDecisionStore _store;

    public HistoricalDecisionRegistry(IHistoricalDecisionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task<string> IngestAsync(
        HistoricalDecision extracted,
        DateTime ingestedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extracted);
        var draft = Copy(extracted, new HistoricalDecisionReview(), ingestedAt);
        return _store.SaveHistoricalDecisionAsync(draft, cancellationToken);
    }

    public async Task<string> ReviewAsync(
        string decisionId,
        int version,
        string actor,
        string reason,
        bool approved,
        HistoricalDecisionEnforcement enforcement,
        DateTime reviewedAt,
        CancellationToken cancellationToken)
    {
        var decisions = await _store.LoadHistoricalDecisionsAsync(cancellationToken);
        var source = decisions.SingleOrDefault(decision =>
                decision.Id.Equals(decisionId, StringComparison.Ordinal) &&
                decision.Version == version) ??
            throw new InvalidOperationException(
                $"Historical decision '{decisionId}' version {version} was not found.");
        var latestVersion = decisions.Where(decision => decision.Id.Equals(
                decisionId,
                StringComparison.Ordinal))
            .Max(decision => decision.Version);
        if (latestVersion != version)
        {
            throw new InvalidOperationException(
                $"Historical decision review is stale; latest version is {latestVersion}.");
        }

        var review = new HistoricalDecisionReview
        {
            Status = approved
                ? HistoricalDecisionReviewStatus.Approved
                : HistoricalDecisionReviewStatus.Rejected,
            Authority = HistoricalDecisionReviewAuthority.Human,
            Actor = actor,
            Reason = reason,
            ReviewedAt = reviewedAt
        };
        var revision = Copy(
            source,
            review,
            reviewedAt,
            version + 1,
            enforcement);
        return await _store.SaveHistoricalDecisionAsync(revision, cancellationToken);
    }

    public Task<string> SuppressAsync(
        HistoricalDecisionSuppression suppression,
        CancellationToken cancellationToken) =>
        _store.SaveHistoricalDecisionSuppressionAsync(suppression, cancellationToken);

    public Task<IReadOnlyList<HistoricalDecision>> ListAsync(
        CancellationToken cancellationToken) =>
        _store.LoadHistoricalDecisionsAsync(cancellationToken);

    private static HistoricalDecision Copy(
        HistoricalDecision source,
        HistoricalDecisionReview review,
        DateTime createdAt,
        int? version = null,
        HistoricalDecisionEnforcement? enforcement = null) => new()
        {
            SchemaVersion = HistoricalDecisionSchema.DecisionVersion,
            Id = source.Id,
            Version = version ?? source.Version,
            Type = source.Type,
            Source = source.Source,
            SourceVersion = source.SourceVersion,
            SourceHash = source.SourceHash,
            Authority = source.Authority,
            ValidFrom = source.ValidFrom,
            ValidUntil = source.ValidUntil,
            ProhibitedPatterns = source.ProhibitedPatterns,
            RequiredPatterns = source.RequiredPatterns,
            Justification = source.Justification,
            Enforcement = enforcement ?? source.Enforcement,
            ExtractedHeuristically = source.ExtractedHeuristically,
            Scope = source.Scope,
            Review = review,
            CreatedAt = createdAt
        };
}
