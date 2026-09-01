using AECS.Application.SemanticLinter;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class HistoricalDecisionRegistryTests
{
    private static readonly DateTime Now = new(2026, 8, 31, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ApprovedBlockingRuleWithoutHumanReview_IsRejected()
    {
        var decision = WithReview(
            Draft("ADR-7", 1, HistoricalDecisionEnforcement.Blocking),
            new HistoricalDecisionReview
            {
                Status = HistoricalDecisionReviewStatus.Approved,
                Authority = HistoricalDecisionReviewAuthority.Policy,
                Actor = "automated-extractor",
                Reason = "Heuristic confidence exceeded threshold.",
                ReviewedAt = Now
            });

        var seal = () => HistoricalDecisionContract.Seal(decision);

        seal.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task IngestAndHumanReview_CreateImmutableDraftAndApprovedRevision()
    {
        var store = new MemoryHistoryStore();
        var registry = new HistoricalDecisionRegistry(store);

        await registry.IngestAsync(Draft("ADR-7", 1), Now, CancellationToken.None);
        await registry.ReviewAsync(
            "ADR-7",
            1,
            "architect@example.com",
            "Source and semantic selector reviewed.",
            approved: true,
            HistoricalDecisionEnforcement.Blocking,
            Now.AddMinutes(5),
            CancellationToken.None);

        store.Decisions.Should().HaveCount(2);
        store.Decisions[0].Review.Status.Should().Be(HistoricalDecisionReviewStatus.Draft);
        store.Decisions[1].Version.Should().Be(2);
        store.Decisions[1].Review.Should().Match<HistoricalDecisionReview>(review =>
            review.Status == HistoricalDecisionReviewStatus.Approved &&
            review.Authority == HistoricalDecisionReviewAuthority.Human &&
            review.Actor == "architect@example.com");
        store.Decisions[1].Enforcement.Should().Be(HistoricalDecisionEnforcement.Blocking);
    }

    private static HistoricalDecision Draft(
        string id,
        int version,
        HistoricalDecisionEnforcement enforcement =
            HistoricalDecisionEnforcement.Advisory) => new()
        {
            Id = id,
            Version = version,
            Type = HistoricalDecisionType.Adr,
            Source = "docs/adr/ADR-007.md",
            SourceVersion = "git:abc123",
            SourceHash = $"sha256:{new string('b', 64)}",
            Authority = "architecture-board",
            ValidFrom = Now.AddDays(-1),
            ProhibitedPatterns =
            [
                new HistoricalDecisionPattern
                {
                    Kind = HistoricalPatternKind.ConstructsType,
                    Value = "UnsafeClient"
                }
            ],
            Justification = "Prevent recurrence of a reviewed architecture violation.",
            Enforcement = enforcement,
            ExtractedHeuristically = true,
            CreatedAt = Now
        };

    private static HistoricalDecision WithReview(
        HistoricalDecision source,
        HistoricalDecisionReview review) => new()
        {
            Id = source.Id,
            Version = source.Version,
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
            Enforcement = source.Enforcement,
            ExtractedHeuristically = source.ExtractedHeuristically,
            Scope = source.Scope,
            Review = review,
            CreatedAt = source.CreatedAt
        };

    private sealed class MemoryHistoryStore : IHistoricalDecisionStore
    {
        public List<HistoricalDecision> Decisions { get; } = [];
        public List<HistoricalDecisionSuppression> Suppressions { get; } = [];

        public Task<string> SaveHistoricalDecisionAsync(
            HistoricalDecision decision,
            CancellationToken cancellationToken)
        {
            Decisions.Add(HistoricalDecisionContract.Seal(decision));
            return Task.FromResult("memory://decision");
        }

        public Task<IReadOnlyList<HistoricalDecision>> LoadHistoricalDecisionsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HistoricalDecision>>(Decisions);

        public Task<string> SaveHistoricalDecisionSuppressionAsync(
            HistoricalDecisionSuppression suppression,
            CancellationToken cancellationToken)
        {
            Suppressions.Add(HistoricalDecisionContract.Seal(suppression));
            return Task.FromResult("memory://suppression");
        }

        public Task<IReadOnlyList<HistoricalDecisionSuppression>>
            LoadHistoricalDecisionSuppressionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HistoricalDecisionSuppression>>(Suppressions);
    }
}
