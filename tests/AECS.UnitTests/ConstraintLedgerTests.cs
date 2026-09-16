using AECS.Application.ConstraintLedger;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class ConstraintLedgerTests
{
    private readonly ConstraintLedgerService _ledger = new();

    private static ConstraintRecord Active(
        string key = "SEC-001",
        ConstraintAuthority authority = ConstraintAuthority.TaskContract,
        int revision = 1,
        ConstraintKind kind = ConstraintKind.SecurityPolicy,
        string description = "No external network access",
        string scope = "src/**") => new()
    {
        RequirementKey = key,
        Revision = revision,
        Description = description,
        Scope = scope,
        Kind = kind,
        Status = ConstraintStatus.Active,
        Authority = authority,
        Origin = ConstraintOrigin.TaskContractYaml,
        Verifiability = ConstraintVerifiability.Deterministic,
        VerifierName = "Scope",
        RepositorySnapshotId = "snap-001",
        ValidFrom = DateTime.UtcNow.AddDays(-1),
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public void Ingest_NewConstraint_IsSealedAndStored()
    {
        var record = _ledger.Ingest(Active());

        record.ContentHash.Should().StartWith("sha256:");
        record.Status.Should().Be(ConstraintStatus.Active);
        _ledger.AllRecords.Should().ContainSingle();
    }

    [Fact]
    public void Ingest_DuplicateSameAuthoritySameRevision_RecordsConflict()
    {
        var first = _ledger.Ingest(Active());
        var second = _ledger.Ingest(Active());

        second.Status.Should().Be(ConstraintStatus.Active);
        _ledger.AllConflicts.Should().ContainSingle(c =>
            c.ConstraintAId == first.Id && c.ConstraintBId == second.Id);
    }

    [Fact]
    public void Ingest_HigherAuthority_AutoSupersedes()
    {
        var lower = _ledger.Ingest(Active(authority: ConstraintAuthority.TaskContract));
        var higher = _ledger.Ingest(Active(authority: ConstraintAuthority.SecurityPolicy));

        _ledger.GetById(lower.Id)!.Status.Should().Be(ConstraintStatus.Superseded);
        _ledger.GetById(higher.Id)!.Status.Should().Be(ConstraintStatus.Active);
    }

    [Fact]
    public void Ingest_HigherRevision_SupersedesPrevious()
    {
        var v1 = _ledger.Ingest(Active(revision: 1));
        var v2 = _ledger.Ingest(Active(revision: 2));

        _ledger.GetById(v1.Id)!.Status.Should().Be(ConstraintStatus.Superseded);
        _ledger.GetById(v2.Id)!.Status.Should().Be(ConstraintStatus.Active);
    }

    [Fact]
    public void Ingest_LowerAuthority_RecordsConflict()
    {
        var higher = _ledger.Ingest(Active(authority: ConstraintAuthority.SecurityPolicy));
        var lower = _ledger.Ingest(Active(authority: ConstraintAuthority.TaskContract));

        // Lower authority should NOT supersede higher
        _ledger.GetById(higher.Id)!.Status.Should().Be(ConstraintStatus.Active);
        _ledger.AllConflicts.Should().ContainSingle(c =>
            c.Reason.Contains("Lower authority"));
    }

    [Fact]
    public void Supersede_ValidTransition_UpdatesBothRecords()
    {
        var original = _ledger.Ingest(Active());
        var replacement = _ledger.Supersede(original.Id, Active(
            description: "Updated: encrypted connections only"));

        _ledger.GetById(original.Id)!.Status.Should().Be(ConstraintStatus.Superseded);
        replacement.Status.Should().Be(ConstraintStatus.Active);
        replacement.SupersedesId.Should().Be(original.Id);
        replacement.Revision.Should().Be(original.Revision + 1);
        replacement.RequirementKey.Should().Be(original.RequirementKey);
    }

    [Fact]
    public void Supersede_LowerAuthority_Throws()
    {
        var original = _ledger.Ingest(Active(authority: ConstraintAuthority.SecurityPolicy));

        var action = () => _ledger.Supersede(original.Id,
            Active(authority: ConstraintAuthority.TaskContract));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*lower than*");
    }

    [Fact]
    public void Supersede_AlreadySuperseded_Throws()
    {
        var first = _ledger.Ingest(Active(revision: 1));
        _ledger.Ingest(Active(revision: 2)); // auto-supersedes first

        var action = () => _ledger.Supersede(first.Id, Active());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Superseded*");
    }

    [Fact]
    public void Revoke_SufficientAuthority_MarksRevoked()
    {
        var record = _ledger.Ingest(Active());
        var revoked = _ledger.Revoke(record.Id,
            ConstraintAuthority.SecurityPolicy, "Policy change");

        revoked.Status.Should().Be(ConstraintStatus.Revoked);
        revoked.ValidUntil.Should().NotBeNull();
    }

    [Fact]
    public void Revoke_InsufficientAuthority_Throws()
    {
        var record = _ledger.Ingest(Active(authority: ConstraintAuthority.SecurityPolicy));

        var action = () => _ledger.Revoke(record.Id,
            ConstraintAuthority.TaskContract, "Not authorized");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*lower than*");
    }

    [Fact]
    public void GetActive_FiltersByStatus()
    {
        var active = _ledger.Ingest(Active(revision: 1));
        _ledger.Ingest(Active(revision: 2)); // supersedes v1

        var result = _ledger.GetActive();

        result.Should().ContainSingle(r => r.Id != active.Id);
        result.Should().NotContain(r => r.Id == active.Id);
    }

    [Fact]
    public void GetActive_FiltersByScope()
    {
        var scopeA = _ledger.Ingest(Active(scope: "src/Billing/**"));
        var scopeB = _ledger.Ingest(Active(key: "SEC-002", scope: "src/Orders/**"));

        var result = _ledger.GetActive("src/Billing/**");

        result.Should().ContainSingle(r => r.Id == scopeA.Id);
    }

    [Fact]
    public void GetHistory_ReturnsAllRevisions()
    {
        _ledger.Ingest(Active(revision: 1));
        _ledger.Ingest(Active(revision: 2));
        _ledger.Ingest(Active(revision: 3));

        var history = _ledger.GetHistory("SEC-001");

        // v1 and v2 are superseded, v3 is active
        history.Should().HaveCount(3);
        history.Select(r => r.Revision).Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void CreateSetRef_ComputesCanonicalHash()
    {
        _ledger.Ingest(Active(key: "SEC-001"));
        _ledger.Ingest(Active(key: "SEC-002"));

        var setRef = _ledger.CreateSetRef();

        setRef.CanonicalSha256.Should().StartWith("sha256:");
        setRef.ActiveCount.Should().Be(2);
        setRef.Revision.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CreateSetRef_DifferentSets_DifferentHashes()
    {
        _ledger.Ingest(Active(key: "SEC-001"));
        var set1 = _ledger.CreateSetRef();

        _ledger.Ingest(Active(key: "SEC-002"));
        var set2 = _ledger.CreateSetRef();

        set1.CanonicalSha256.Should().NotBe(set2.CanonicalSha256);
    }

    [Fact]
    public void DetectConflicts_FindsActiveConflicts()
    {
        _ledger.Ingest(Active(revision: 1));
        _ledger.Ingest(Active(revision: 1)); // duplicate

        // Conflict is recorded during Ingest, not during DetectConflicts
        _ledger.AllConflicts.Should().ContainSingle(c => c.Reason.Contains("Duplicate constraint"));
    }

    [Fact]
    public void ValidateContract_UnresolvedConflicts_BlockApproval()
    {
        _ledger.Ingest(Active(revision: 1));
        _ledger.Ingest(Active(revision: 1)); // duplicate → conflict
        _ledger.DetectConflicts();

        var contract = new TaskContract { Id = "T1", Objective = "Test" };
        var violations = _ledger.ValidateContract(contract);

        violations.Should().Contain(v => v.Contains("unresolved constraint conflict"));
    }

    [Fact]
    public void ValidateContract_NoConflicts_NoViolations()
    {
        _ledger.Ingest(Active(key: "SEC-001"));

        var contract = new TaskContract { Id = "T1", Objective = "Test" };
        var violations = _ledger.ValidateContract(contract);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Seal_ProducesStableContentHash()
    {
        var record = Active();
        var sealed1 = ConstraintRecordContract.Seal(record);
        var sealed2 = ConstraintRecordContract.Seal(record);

        sealed1.ContentHash.Should().Be(sealed2.ContentHash);
    }

    [Fact]
    public void Seal_DifferentRecords_DifferentHashes()
    {
        var a = ConstraintRecordContract.Seal(Active(key: "SEC-001"));
        var b = ConstraintRecordContract.Seal(Active(key: "SEC-002"));

        a.ContentHash.Should().NotBe(b.ContentHash);
    }

    [Fact]
    public void Validate_MissingRequirementKey_Throws()
    {
        var record = new ConstraintRecord
        {
            RequirementKey = "",
            Revision = 1,
            Description = "Test",
            Kind = ConstraintKind.SecurityPolicy,
            Status = ConstraintStatus.Active,
            Authority = ConstraintAuthority.TaskContract,
            Origin = ConstraintOrigin.TaskContractYaml,
            Verifiability = ConstraintVerifiability.Deterministic,
            RepositorySnapshotId = "snap-001",
            ValidFrom = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        var action = () => ConstraintRecordContract.Seal(record);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_ZeroRevision_Throws()
    {
        var record = new ConstraintRecord
        {
            RequirementKey = "SEC-001",
            Revision = 0,
            Description = "Test",
            Kind = ConstraintKind.SecurityPolicy,
            Status = ConstraintStatus.Active,
            Authority = ConstraintAuthority.TaskContract,
            Origin = ConstraintOrigin.TaskContractYaml,
            Verifiability = ConstraintVerifiability.Deterministic,
            RepositorySnapshotId = "snap-001",
            ValidFrom = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        var action = () => ConstraintRecordContract.Seal(record);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MultipleConstraintKinds_AreTrackedIndependently()
    {
        _ledger.Ingest(Active(key: "SEC-001", kind: ConstraintKind.SecurityPolicy));
        _ledger.Ingest(Active(key: "BUD-001", kind: ConstraintKind.BudgetLimit));
        _ledger.Ingest(Active(key: "SCP-001", kind: ConstraintKind.ScopeBoundary));

        var active = _ledger.GetActive();

        active.Should().HaveCount(3);
        active.Select(r => r.Kind).Should().BeEquivalentTo(new[]
        {
            ConstraintKind.SecurityPolicy,
            ConstraintKind.BudgetLimit,
            ConstraintKind.ScopeBoundary
        });
    }

    [Fact]
    public void Supersede_PreservesHistory()
    {
        var v1 = _ledger.Ingest(Active(revision: 1));
        var v2 = _ledger.Supersede(v1.Id, Active(revision: 2));

        var history = _ledger.GetHistory("SEC-001");

        history.Should().HaveCount(2);
        history[0].Status.Should().Be(ConstraintStatus.Superseded);
        history[1].Status.Should().Be(ConstraintStatus.Active);
        history[1].SupersedesId.Should().Be(v1.Id);
    }
}
