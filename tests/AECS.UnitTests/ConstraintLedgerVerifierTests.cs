using AECS.Application.ConstraintLedger;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class ConstraintLedgerVerifierTests
{
    private static readonly VerificationContext Context = new()
    {
        TaskId = "T-VERIFY",
        AgentRunId = "run-1"
    };

    private static ConstraintRecord Record(
        string key,
        ConstraintVerifiability verifiability,
        string? verifierName,
        Guid? id = null,
        string description = "test constraint") => ConstraintRecordContract.Seal(new ConstraintRecord
        {
            Id = id ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0001"),
            RequirementKey = key,
            Revision = 1,
            Description = description,
            Scope = "test",
            Kind = ConstraintKind.VerificationRequirement,
            Status = ConstraintStatus.Active,
            Authority = ConstraintAuthority.TaskContract,
            Origin = ConstraintOrigin.TaskContractYaml,
            Verifiability = verifiability,
            VerifierName = verifierName,
            RepositorySnapshotId = "sha256:snapshot",
            ValidFrom = ConstraintLedgerProjector.Epoch,
            CreatedAt = ConstraintLedgerProjector.Epoch
        });

    private static ConstraintLedgerService Service(params ConstraintRecord[] records)
    {
        var service = new ConstraintLedgerService();
        foreach (var record in records)
        {
            service.Ingest(record);
        }

        return service;
    }

    private static VerificationResult Pass(string verifier) => new()
    {
        Verifier = verifier,
        Status = VerificationStatus.Pass,
        Severity = Severity.Info,
        Message = "OK"
    };

    private static VerificationResult Fail(string verifier) => new()
    {
        Verifier = verifier,
        Status = VerificationStatus.Fail,
        Severity = Severity.Error,
        Message = "Failed"
    };

    [Fact]
    public async Task Verify_DeterministicConstraintSatisfied_ReturnsPass()
    {
        var ledger = Service(Record("verification.build",
            ConstraintVerifiability.Deterministic, "Build"));
        var set = ledger.CreateSetRef();
        var verifier = new ConstraintLedgerVerifier(ledger, set);

        var result = await verifier.VerifyAsync(Context, [Pass("Build")], CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Pass);
        result.ConstraintLedger!.HasViolations.Should().BeFalse();
        result.ConstraintLedger.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.Satisfied);
    }

    [Fact]
    public async Task Verify_NamedVerifierFailed_ReturnsCriticalFailure()
    {
        var ledger = Service(Record("verification.build",
            ConstraintVerifiability.Deterministic, "Build"));
        var set = ledger.CreateSetRef();
        var verifier = new ConstraintLedgerVerifier(ledger, set);

        var result = await verifier.VerifyAsync(Context, [Fail("Build")], CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Severity.Should().Be(Severity.Critical);
        result.ConstraintLedger!.HasViolations.Should().BeTrue();
        result.ConstraintLedger.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.Violated);
    }

    [Fact]
    public async Task Verify_NamedVerifierMissing_ReturnsViolation()
    {
        var ledger = Service(Record("verification.tests",
            ConstraintVerifiability.Deterministic, "Tests"));
        var set = ledger.CreateSetRef();
        var verifier = new ConstraintLedgerVerifier(ledger, set);

        var result = await verifier.VerifyAsync(Context, [], CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Message.Should().Contain("no result");
    }

    [Fact]
    public async Task Verify_ManualConstraint_SurfacesPendingReviewAndNeverClaimsSatisfied()
    {
        var ledger = Service(Record("approval.production",
            ConstraintVerifiability.Manual, null));
        var set = ledger.CreateSetRef();
        var verifier = new ConstraintLedgerVerifier(ledger, set);

        var result = await verifier.VerifyAsync(Context, [], CancellationToken.None);

        result.ConstraintLedger!.HasViolations.Should().BeFalse();
        result.ConstraintLedger.HasPendingReview.Should().BeTrue();
        result.ConstraintLedger.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.PendingReview);
        result.Message.Should().Contain("pending manual review");
    }

    [Fact]
    public async Task Verify_UnresolvedConflict_ReturnsCriticalFailure()
    {
        var ledger = Service(
            Record("dup", ConstraintVerifiability.Deterministic, "Build",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0001"),
                "first version"),
            Record("dup", ConstraintVerifiability.Deterministic, "Build",
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0002"),
                "conflicting version"));
        var set = ledger.CreateSetRef();
        var verifier = new ConstraintLedgerVerifier(ledger, set);

        var result = await verifier.VerifyAsync(Context, [Pass("Build")], CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Severity.Should().Be(Severity.Critical);
        result.ConstraintLedger!.UnresolvedConflictCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Verify_EvidenceCarriesSetBinding()
    {
        var ledger = Service(Record("budget.limits",
            ConstraintVerifiability.Deterministic, "Budget"));
        var set = ledger.CreateSetRef();
        var verifier = new ConstraintLedgerVerifier(ledger, set);

        var result = await verifier.VerifyAsync(Context, [Pass("Budget")], CancellationToken.None);

        result.ConstraintLedger!.SetId.Should().Be(set.Id);
        result.ConstraintLedger.SetCanonicalSha256.Should().Be(set.CanonicalSha256);
        result.ConstraintLedger.ActiveCount.Should().Be(1);
    }
}
