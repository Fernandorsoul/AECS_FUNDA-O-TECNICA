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
        string description = "test constraint",
        ConstraintKind kind = ConstraintKind.VerificationRequirement) =>
        ConstraintRecordContract.Seal(new ConstraintRecord
        {
            Id = id ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0001"),
            RequirementKey = key,
            Revision = 1,
            Description = description,
            Scope = "test",
            Kind = kind,
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

    private static ConstraintRecord NoNetworkRecord() => Record(
        "process.no-network",
        ConstraintVerifiability.ProcessInvariant,
        TrajectoryVerifierNames.NoNetwork,
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0002"),
        "No network during execution",
        ConstraintKind.ProcessInvariant);

    private static TrajectoryEvidence DockerTrajectory(
        string[]? phases = null,
        string[]? destinations = null) => new()
    {
        Runtime = "docker",
        NetworkAllowedPhases = phases?.ToList() ?? [],
        NetworkDestinations = destinations?.ToList() ?? [],
        CapturedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Verify_ProcessInvariant_NoNetwork_DockerWithoutNetwork_Satisfied()
    {
        var ledger = Service(NoNetworkRecord());
        var verifier = new ConstraintLedgerVerifier(ledger, ledger.CreateSetRef());
        var context = new VerificationContext
        {
            TaskId = "T-INV",
            AgentRunId = "inv-1",
            Trajectory = DockerTrajectory()
        };

        var result = await verifier.VerifyAsync(context, [], CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Pass);
        result.ConstraintLedger!.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.Satisfied);
    }

    [Fact]
    public async Task Verify_ProcessInvariant_NoNetwork_DockerWithNetwork_Violated()
    {
        var ledger = Service(NoNetworkRecord());
        var verifier = new ConstraintLedgerVerifier(ledger, ledger.CreateSetRef());
        var context = new VerificationContext
        {
            TaskId = "T-INV",
            AgentRunId = "inv-2",
            Trajectory = DockerTrajectory(
                phases: ["candidate.build"],
                destinations: ["api.nuget.org"])
        };

        var result = await verifier.VerifyAsync(context, [], CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Severity.Should().Be(Severity.Critical);
        result.ConstraintLedger!.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.Violated);
    }

    [Fact]
    public async Task Verify_ProcessInvariant_NoNetwork_HostRuntime_StaysPending()
    {
        var ledger = Service(NoNetworkRecord());
        var verifier = new ConstraintLedgerVerifier(ledger, ledger.CreateSetRef());
        var context = new VerificationContext
        {
            TaskId = "T-INV",
            AgentRunId = "inv-3",
            Trajectory = new TrajectoryEvidence
            {
                Runtime = "host",
                CapturedAt = DateTime.UtcNow
            }
        };

        var result = await verifier.VerifyAsync(context, [], CancellationToken.None);

        result.ConstraintLedger!.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.PendingReview);
        result.ConstraintLedger.HasViolations.Should().BeFalse();
    }

    [Fact]
    public async Task Verify_ProcessInvariant_NoTrajectory_StaysPending()
    {
        var ledger = Service(NoNetworkRecord());
        var verifier = new ConstraintLedgerVerifier(ledger, ledger.CreateSetRef());

        var result = await verifier.VerifyAsync(Context, [], CancellationToken.None);

        result.ConstraintLedger!.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.PendingReview);
    }

    [Fact]
    public async Task Verify_ProcessInvariant_WithoutTrajectoryVerifier_StaysPending()
    {
        var ledger = Service(Record(
            "process.custom-invariant",
            ConstraintVerifiability.ProcessInvariant,
            null,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0003"),
            "custom",
            ConstraintKind.ProcessInvariant));
        var verifier = new ConstraintLedgerVerifier(ledger, ledger.CreateSetRef());
        var context = new VerificationContext
        {
            TaskId = "T-INV",
            AgentRunId = "inv-4",
            Trajectory = DockerTrajectory()
        };

        var result = await verifier.VerifyAsync(context, [], CancellationToken.None);

        result.ConstraintLedger!.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.PendingReview);
    }
}
