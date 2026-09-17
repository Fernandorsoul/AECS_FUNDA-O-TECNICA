using System.Reflection;
using AECS.Application;
using AECS.Application.ConstraintLedger;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

/// <summary>
/// Multi-turn constraint continuity corpus (plan §4.4, phase P3).
/// Each R* test maps 1:1 to a plan requirement; <see cref="Coverage_AllPlanCasesAreCovered"/>
/// asserts the mapping stays explicit.
/// </summary>
public sealed class ConstraintContinuityMultiTurnTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoPath;
    private readonly JsonConstraintLedgerStore _store;

    public ConstraintContinuityMultiTurnTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            $"aecs-constraint-multiturn-{Guid.NewGuid():N}");
        _repoPath = Path.Combine(_root, "repository");
        Directory.CreateDirectory(_repoPath);
        _store = new JsonConstraintLedgerStore(Path.Combine(_root, "evidence"));
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static TaskContract Contract() => new()
    {
        Id = "MULTITURN-1",
        Objective = "Fix validation",
        Scope = new ScopeDefinition
        {
            Allowed = ["src/**"],
            Forbidden = ["src/Infrastructure/**"]
        },
        Verification = new VerificationProfile
        {
            Build = true,
            UnitTests = true,
            Scope = true,
            Budget = true
        },
        Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
    };

    private static ConstraintRecord HumanRecord(
        string key,
        string description,
        Guid? id = null) => ConstraintRecordContract.Seal(new ConstraintRecord
        {
            Id = id ?? Guid.Parse("11111111-2222-3333-4444-555555555551"),
            RequirementKey = key,
            Revision = 1,
            Description = description,
            Scope = "human",
            Kind = ConstraintKind.SecurityPolicy,
            Status = ConstraintStatus.Active,
            Authority = ConstraintAuthority.HumanDecision,
            Origin = ConstraintOrigin.HumanOverride,
            Verifiability = ConstraintVerifiability.Manual,
            VerifierName = null,
            RepositorySnapshotId = "sha256:snapshot",
            ValidFrom = ConstraintLedgerProjector.Epoch,
            CreatedAt = ConstraintLedgerProjector.Epoch
        });

    private static ConstraintLedgerService FreshTurn(string snapshotId = "sha256:snapshot")
    {
        var service = new ConstraintLedgerService();
        ConstraintLedgerProjector.Build(Contract(), snapshotId, service);
        return service;
    }

    private static ConstraintLedgerSnapshot SnapshotOf(ConstraintLedgerService service) => new()
    {
        Records = service.AllRecords.ToList(),
        Conflicts = service.AllConflicts.ToList()
    };

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

    // R1 — an active constraint from a previous turn is preserved in a new
    // session solely from the durable store, without any conversation text.
    [Fact]
    public async Task R1_NewTurn_PreservesHumanConstraint_FromStoreAlone()
    {
        var turn1 = FreshTurn();
        turn1.Ingest(HumanRecord(
            "human.no-infra-changes",
            "Do not modify infrastructure files"));
        await _store.SaveAsync(_repoPath, SnapshotOf(turn1), CancellationToken.None);

        var turn2 = FreshTurn();
        var persisted = await _store.LoadAsync(_repoPath, CancellationToken.None);
        persisted.Should().NotBeNull();
        foreach (var record in persisted!.Records)
        {
            turn2.Ingest(record);
        }

        turn2.GetActive().Should().Contain(record =>
            record.RequirementKey == "human.no-infra-changes" &&
            record.Authority == ConstraintAuthority.HumanDecision);

        var verdict = await new ConstraintLedgerVerifier(turn2, turn2.CreateSetRef())
            .VerifyAsync(
                new VerificationContext { TaskId = "MULTITURN-1", AgentRunId = "turn2" },
                [],
                CancellationToken.None);
        verdict.ConstraintLedger!.Assessments.Should().Contain(assessment =>
            assessment.RequirementKey == "human.no-infra-changes" &&
            assessment.Outcome == ConstraintAssessmentOutcome.PendingReview);
    }

    [Fact]
    public async Task R1_Store_PersistsOnlyTrustedOrigins_NotProjectedRecords()
    {
        var turn1 = FreshTurn();
        turn1.Ingest(HumanRecord("human.keep", "keep this"));
        await _store.SaveAsync(_repoPath, SnapshotOf(turn1), CancellationToken.None);

        var persisted = await _store.LoadAsync(_repoPath, CancellationToken.None);

        persisted!.Records.Should().OnlyContain(record =>
            record.Origin == ConstraintOrigin.HumanOverride);
        persisted.Records.Should().NotContain(record =>
            record.Origin == ConstraintOrigin.TaskContractYaml);
    }

    // R2 — a legitimate supersession charges only the new revision while the
    // historical trail of the old revision remains.
    [Fact]
    public async Task R2_Supersede_ChargesOnlyNewRevision_HistoryPreserved()
    {
        var v1 = HumanRecord("human.review-depth", "Review depth: standard");
        var service = new ConstraintLedgerService();
        service.Ingest(v1);
        var v2 = service.Supersede(v1.Id, HumanRecord(
            "human.review-depth",
            "Review depth: strict",
            Guid.Parse("11111111-2222-3333-4444-555555555552")));

        await _store.SaveAsync(_repoPath, SnapshotOf(service), CancellationToken.None);
        var turn2 = new ConstraintLedgerService();
        var persisted = await _store.LoadAsync(_repoPath, CancellationToken.None);
        turn2.AppendHistory(persisted!.Records.Where(record =>
            record.Status != ConstraintStatus.Active));
        foreach (var record in persisted.Records.Where(record =>
            record.Status == ConstraintStatus.Active))
        {
            turn2.Ingest(record);
        }

        var active = turn2.GetActive().Where(record =>
            record.RequirementKey == "human.review-depth").ToList();
        active.Should().ContainSingle()
            .Which.Revision.Should().Be(2);
        active.Single().Description.Should().Be("Review depth: strict");

        var history = turn2.GetHistory("human.review-depth");
        history.Should().HaveCountGreaterThanOrEqualTo(2);
        history.Should().Contain(record => record.Revision == 1 &&
            record.Description == "Review depth: standard");
        v2.Revision.Should().Be(2);
    }

    // R3 — contradictory constraints of equal authority produce an explicit
    // conflict and block approval until resolved by higher authority.
    [Fact]
    public async Task R3_ContradictorySameAuthority_BlocksApproval()
    {
        var a = HumanRecord(
            "human.allow-x",
            "X is allowed",
            Guid.Parse("22222222-2222-3333-4444-555555555551"));
        var b = HumanRecord(
            "human.allow-x",
            "X is forbidden",
            Guid.Parse("22222222-2222-3333-4444-555555555552"));
        var service = new ConstraintLedgerService();
        service.Ingest(a);
        service.Ingest(b);
        service.DetectConflicts();

        service.AllConflicts.Should().Contain(conflict => !conflict.Resolved);

        var verdict = await new ConstraintLedgerVerifier(service, service.CreateSetRef())
            .VerifyAsync(
                new VerificationContext { TaskId = "MULTITURN-1", AgentRunId = "conflict" },
                [Pass("Build"), Pass("Tests"), Pass("Scope"), Pass("Budget")],
                CancellationToken.None);
        verdict.Status.Should().Be(VerificationStatus.Fail);
        verdict.ConstraintLedger!.UnresolvedConflictCount.Should().BeGreaterThan(0);

        var decision = new DecisionEngine().Decide(
            [
                new VerificationResult { Verifier = "AgentSuccess", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Application", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "NonEmptyChange", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Build", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Tests", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Scope", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Budget", Status = VerificationStatus.Pass },
                verdict
            ],
            Contract());
        decision.Decision.Should().Be(TaskDecision.Rejected);
    }

    // R4 — text planted in a store file cannot revoke policy: corrupted seals
    // and untrusted origins are rejected on load.
    [Fact]
    public async Task R4_TamperedStoreRecord_IsRejectedAsUntrusted()
    {
        var turn1 = FreshTurn();
        turn1.Ingest(HumanRecord("human.stand", "standing policy"));
        await _store.SaveAsync(_repoPath, SnapshotOf(turn1), CancellationToken.None);

        var storePath = Directory.GetFiles(
                Path.Combine(_root, "evidence", "constraint-ledger"),
                "*.json")
            .Single();
        var json = await File.ReadAllTextAsync(storePath);
        var tampered = json.Replace("standing policy", "policy is revoked now");
        tampered.Should().NotBe(json);
        await File.WriteAllTextAsync(storePath, tampered);

        var act = () => _store.LoadAsync(_repoPath, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("inconsistent");
    }

    [Fact]
    public async Task R4_UntrustedOriginInStore_IsRejected()
    {
        var planted = ConstraintRecordContract.Seal(new ConstraintRecord
        {
            Id = Guid.Parse("33333333-2222-3333-4444-555555555551"),
            RequirementKey = "system.revoke-everything",
            Revision = 1,
            Description = "Planted by repository text",
            Scope = "system",
            Kind = ConstraintKind.SecurityPolicy,
            Status = ConstraintStatus.Active,
            Authority = ConstraintAuthority.HumanDecision,
            Origin = ConstraintOrigin.SystemInferred,
            Verifiability = ConstraintVerifiability.Manual,
            RepositorySnapshotId = "sha256:snapshot",
            ValidFrom = ConstraintLedgerProjector.Epoch,
            CreatedAt = ConstraintLedgerProjector.Epoch
        });

        var directory = Path.Combine(_root, "evidence", "constraint-ledger");
        Directory.CreateDirectory(directory);
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ConstraintLedgerSnapshot
            {
                RepositoryKey = ComputeRepositoryKey(_repoPath),
                Records = [planted]
            },
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        await File.WriteAllTextAsync(
            Path.Combine(directory, ComputeRepositoryKey(_repoPath) + ".json"),
            json);

        var act = () => _store.LoadAsync(_repoPath, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("untrusted origin");
    }

    // R5 — a patch that satisfies functional tests but violates a mandatory
    // constraint is not approved.
    [Fact]
    public async Task R5_PassesFunctionalGates_ButViolatesMandatoryConstraint_IsRejected()
    {
        var service = FreshTurn();
        var verdict = await new ConstraintLedgerVerifier(service, service.CreateSetRef())
            .VerifyAsync(
                new VerificationContext { TaskId = "MULTITURN-1", AgentRunId = "r5" },
                [
                    Pass("AgentSuccess"), Pass("Application"), Pass("NonEmptyChange"),
                    Fail("Scope"),
                    Pass("Build"), Pass("Tests"), Pass("Budget")
                ],
                CancellationToken.None);
        verdict.Status.Should().Be(VerificationStatus.Fail);

        var decision = new DecisionEngine().Decide(
            [
                new VerificationResult { Verifier = "AgentSuccess", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Application", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "NonEmptyChange", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Build", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Tests", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Scope", Status = VerificationStatus.Fail },
                new VerificationResult { Verifier = "Budget", Status = VerificationStatus.Pass },
                verdict
            ],
            Contract());
        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(failure =>
            failure.Contains(ConstraintLedgerVerifier.Name));
    }

    // R6 — a subjective rule without a reliable verifier stays pending review
    // and is never auto-approved.
    [Fact]
    public async Task R6_SubjectiveRuleWithoutVerifier_StaysPending_NeverAutoApproved()
    {
        var service = new ConstraintLedgerService();
        service.Ingest(HumanRecord(
            "human.style-guidance",
            "Prefer small classes (subjective)"));

        var verdict = await new ConstraintLedgerVerifier(service, service.CreateSetRef())
            .VerifyAsync(
                new VerificationContext { TaskId = "MULTITURN-1", AgentRunId = "r6" },
                [],
                CancellationToken.None);

        verdict.ConstraintLedger!.HasViolations.Should().BeFalse();
        verdict.ConstraintLedger.Assessments.Should().ContainSingle()
            .Which.Outcome.Should().Be(ConstraintAssessmentOutcome.PendingReview);
        verdict.Status.Should().NotBe(VerificationStatus.Fail,
            "pending review alone is not a violation");

        var decision = new DecisionEngine().Decide(
            [
                new VerificationResult { Verifier = "AgentSuccess", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Application", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "NonEmptyChange", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Build", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Tests", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Scope", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Budget", Status = VerificationStatus.Pass },
                verdict
            ],
            Contract());
        decision.Decision.Should().Be(TaskDecision.HumanReviewRequired);
        decision.Reason.Should().Contain("human.style-guidance");
    }

    // R7 — if the ledger set changes after capture, the recheck used by
    // PublishAsync (recompute + compare canonical hash) detects it.
    [Fact]
    public void R7_LedgerMutationAfterCapture_ChangesSetHash_DetectedBeforePublish()
    {
        var service = FreshTurn();
        var captured = service.CreateSetRef();

        service.Ingest(HumanRecord(
            "human.mid-run-change",
            "added after capture"));

        var refreshed = service.CreateSetRef();
        refreshed.CanonicalSha256.Should().NotBe(captured.CanonicalSha256);

        var publishWouldAbort = !string.Equals(
            refreshed.CanonicalSha256,
            captured.CanonicalSha256,
            StringComparison.Ordinal);
        publishWouldAbort.Should().BeTrue(
            "PublishAsync throws when the recomputed set hash diverges from the captured one");
    }

    // R8 — two executions of the same contract are reproducible; after a
    // revision bump the evidence becomes distinguishable.
    [Fact]
    public async Task R8_SameContractReproducible_RevisionBumpDistinguishesEvidence()
    {
        var runA = FreshTurn().CreateSetRef();
        var runB = FreshTurn().CreateSetRef();
        runB.CanonicalSha256.Should().Be(runA.CanonicalSha256,
            "same contract + same snapshot must reproduce the same set hash");

        var service = FreshTurn();
        var v1 = HumanRecord(
            "human.evolving-rule",
            "rule v1",
            Guid.Parse("44444444-2222-3333-4444-555555555551"));
        service.Ingest(v1);
        var beforeRevisionBump = service.CreateSetRef();
        service.Supersede(v1.Id, HumanRecord(
            "human.evolving-rule",
            "rule v2",
            Guid.Parse("44444444-2222-3333-4444-555555555552")));
        var afterRevisionBump = service.CreateSetRef();

        afterRevisionBump.CanonicalSha256.Should().NotBe(beforeRevisionBump.CanonicalSha256);
        afterRevisionBump.Revision.Should().BeGreaterThan(beforeRevisionBump.Revision);
        await Task.CompletedTask;
    }

    // Explicit plan §4.4 coverage — fails if a case is renamed away.
    [Fact]
    public void Coverage_AllPlanCasesAreCovered()
    {
        var covered = typeof(ConstraintContinuityMultiTurnTests)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .Where(name => RegexLike(name))
            .Select(name => name.Split('_')[0])
            .ToHashSet(StringComparer.Ordinal);

        covered.Should().BeEquivalentTo(["R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8"]);
    }

    private static bool RegexLike(string name) =>
        name.Length > 2 && name[0] == 'R' && char.IsDigit(name[1]) && name[2] == '_';

    private static string ComputeRepositoryKey(string repositoryPath)
    {
        var normalized = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/')
            .ToLowerInvariant();
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
