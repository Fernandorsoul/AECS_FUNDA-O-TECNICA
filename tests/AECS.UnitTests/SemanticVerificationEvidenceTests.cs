using AECS.Application.SemanticLinter;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class SemanticVerificationEvidenceTests
{
    [Fact]
    public void Evidence_RecordsBaselineCandidateImpactAndFindingExplanation()
    {
        var symbol = SemanticGraphFixture.Node(
            "changed-method",
            "member",
            "Save",
            "void Orders.Save()",
            documentationId: "M:Orders.Save");
        var input = SemanticGraphFixture.Input([symbol], [symbol]);

        var evidence = SemanticEvidenceFactory.Create(input,
        [
            new SemanticRuleFinding
            {
                RuleId = "EB003-CHANGED-SIGNATURE",
                SymbolId = symbol.Id,
                Symbol = symbol.DisplayName,
                FilePath = "src/App/Changed.cs",
                Severity = RuleSeverity.Critical,
                Baseline = SemanticGraphFixture.Commit,
                Justification = "The resolved public signature changed."
            }
        ]);

        evidence.SchemaVersion.Should().Be(SemanticVerificationEvidenceSchema.Version);
        evidence.BaselineCommit.Should().Be(SemanticGraphFixture.Commit);
        evidence.BaselineSnapshotHash.Should().Be("sha256:baseline");
        evidence.CandidateSnapshotHash.Should().Be("sha256:candidate");
        evidence.BaselineGraphHash.Should().Be("sha256:baseline-graph");
        evidence.CandidateGraphHash.Should().Be("sha256:candidate-graph");
        evidence.ImpactedFiles.Should().Equal("src/App/Changed.cs");
        evidence.Findings.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new SemanticFindingEvidence
            {
                RuleId = "EB003-CHANGED-SIGNATURE",
                SymbolId = symbol.Id,
                Symbol = symbol.DisplayName,
                FilePath = "src/App/Changed.cs",
                Severity = "Critical",
                Baseline = SemanticGraphFixture.Commit,
                Justification = "The resolved public signature changed."
            });
    }

    [Fact]
    public async Task RequiredSemanticInputsUnavailable_AllCriticalVerifiersFailClosed()
    {
        IVerifier[] verifiers =
        [
            new EB001Verifier(),
            new EB002Verifier(),
            new EB003Verifier(),
            new EB004Verifier()
        ];
        var context = new VerificationContext
        {
            AgentRunId = "run",
            SemanticAnalysisError = "Candidate semantic graph could not be prepared."
        };

        foreach (var verifier in verifiers)
        {
            var result = await verifier.VerifyAsync(context, CancellationToken.None);

            result.Status.Should().Be(VerificationStatus.Error, verifier.Name);
            result.Severity.Should().Be(Severity.Critical, verifier.Name);
            result.Message.Should().Contain("Candidate semantic graph could not be prepared.");
        }
    }

    [Theory]
    [InlineData("roslyn-msbuild-symbol-graph/v1", true)]
    [InlineData("roslyn-msbuild-symbol-graph/v2", true)]
    [InlineData("roslyn-msbuild-symbol-graph/v3", false)]
    public void SymbolGraphStrategy_OnlyAcceptsKnownVersions(
        string strategyVersion,
        bool expected)
    {
        CSharpSymbolGraphSchema.IsSupportedStrategyVersion(strategyVersion)
            .Should().Be(expected);
    }
}
