using System.Text.Json;
using System.Text.Json.Nodes;
using AECS.Application.SemanticLinter;
using AECS.Domain.Models;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class HistoricalDecisionStoreTests
{
    [Fact]
    public async Task JsonStore_PersistsReviewAndSuppressionAcrossRestartAndRejectsTampering()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"aecs-history-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonExecutionEvidenceStore(
                Path.Combine(root, "evidence"),
                Path.Combine(root, "keys"));
            var registry = new HistoricalDecisionRegistry(store);
            var now = new DateTime(2026, 8, 31, 20, 0, 0, DateTimeKind.Utc);
            await registry.IngestAsync(Draft(now), now, CancellationToken.None);
            await registry.ReviewAsync(
                "INC-DB-1",
                1,
                "security@example.com",
                "Incident source and selector reviewed.",
                approved: true,
                HistoricalDecisionEnforcement.Blocking,
                now.AddMinutes(1),
                CancellationToken.None);
            await registry.SuppressAsync(new HistoricalDecisionSuppression
            {
                Id = "SUP-DB-1",
                Version = 1,
                DecisionId = "INC-DB-1",
                DecisionVersion = 2,
                Actor = "incident-owner",
                Reason = "Controlled compatibility window.",
                FilePath = "src/App/Changed.cs",
                CreatedAt = now.AddMinutes(2),
                ExpiresAt = now.AddDays(2)
            }, CancellationToken.None);

            var restarted = new JsonExecutionEvidenceStore(
                Path.Combine(root, "evidence"),
                Path.Combine(root, "keys"));
            (await restarted.LoadHistoricalDecisionsAsync(CancellationToken.None))
                .Should().HaveCount(2).And.OnlyContain(decision =>
                    decision.ContentHash.StartsWith("sha256:", StringComparison.Ordinal));
            (await restarted.LoadHistoricalDecisionSuppressionsAsync(
                CancellationToken.None)).Should().ContainSingle(suppression =>
                    suppression.Actor == "incident-owner" && suppression.Version == 1);

            var decisionPath = Directory.EnumerateFiles(
                    Path.Combine(root, "evidence", "historical-decisions", "decisions"),
                    "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Last();
            var document = JsonNode.Parse(await File.ReadAllTextAsync(decisionPath))!.AsObject();
            document["authority"] = "forged-authority";
            await File.WriteAllTextAsync(
                decisionPath,
                document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var load = () => restarted.LoadHistoricalDecisionsAsync(CancellationToken.None);
            await load.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith(
                    "aecs-history-store-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete unexpected history fixture path: {resolved}");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static HistoricalDecision Draft(DateTime now) => new()
    {
        Id = "INC-DB-1",
        Version = 1,
        Type = HistoricalDecisionType.Incident,
        Source = "incidents/INC-DB-1.json",
        SourceVersion = "incident:v3",
        SourceHash = $"sha256:{new string('c', 64)}",
        Authority = "security-team",
        ValidFrom = now.AddDays(-1),
        ProhibitedPatterns =
        [
            new HistoricalDecisionPattern
            {
                Kind = HistoricalPatternKind.ConstructsType,
                Value = "UnsafeDbClient"
            }
        ],
        Justification = "The client caused a reviewed production incident.",
        Enforcement = HistoricalDecisionEnforcement.Advisory,
        ExtractedHeuristically = true,
        CreatedAt = now
    };
}
