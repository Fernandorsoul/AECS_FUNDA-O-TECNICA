using AECS.Application.ConstraintLedger;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class ConstraintLedgerProjectorTests
{
    private static TaskContract Contract(
        ApprovalLevel approval = ApprovalLevel.None,
        bool databaseMigration = false) => new()
    {
        Id = "T-LEDGER",
        Objective = "Fix validation",
        ContractFingerprint = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        SchemaVersion = TaskContractSchema.CurrentVersion,
        Scope = new ScopeDefinition
        {
            Allowed = ["src/Domain/**"],
            Forbidden = ["src/Infrastructure/**"]
        },
        Constraints = new TaskConstraints
        {
            DatabaseMigration = databaseMigration,
            ExternalDependency = false
        },
        Verification = new VerificationProfile
        {
            Build = true,
            UnitTests = true,
            Scope = true,
            Budget = true
        },
        Approval = new ApprovalPolicy { Production = approval }
    };

    [Fact]
    public void Build_SameContractAndSnapshot_ProducesIdenticalSetHash()
    {
        var contract = Contract();
        var first = ConstraintLedgerProjector.Build(contract, "sha256:snapshot").CreateSetRef();
        var second = ConstraintLedgerProjector.Build(contract, "sha256:snapshot").CreateSetRef();

        second.CanonicalSha256.Should().Be(first.CanonicalSha256);
        second.ActiveCount.Should().Be(first.ActiveCount);
        second.ActiveCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_DifferentSnapshot_ProducesDifferentSetHash()
    {
        var contract = Contract();
        var first = ConstraintLedgerProjector.Build(contract, "sha256:snapshot-a").CreateSetRef();
        var second = ConstraintLedgerProjector.Build(contract, "sha256:snapshot-b").CreateSetRef();

        second.CanonicalSha256.Should().NotBe(first.CanonicalSha256);
    }

    [Fact]
    public void Project_MapsScopeBudgetAndVerificationToDeterministicVerifiers()
    {
        var records = ConstraintLedgerProjector.Project(Contract(), "sha256:snapshot").ToList();

        records.Should().Contain(record =>
            record.RequirementKey == "scope.forbidden" &&
            record.Verifiability == ConstraintVerifiability.Deterministic &&
            record.VerifierName == "Scope");
        records.Should().Contain(record =>
            record.RequirementKey == "scope.allowed" &&
            record.VerifierName == "Scope");
        records.Should().Contain(record =>
            record.RequirementKey == "budget.limits" &&
            record.VerifierName == "Budget");
        records.Should().Contain(record =>
            record.RequirementKey == "verification.build" &&
            record.VerifierName == "Build");
        records.Should().Contain(record =>
            record.RequirementKey == "verification.tests" &&
            record.VerifierName == "Tests");
        records.Should().OnlyContain(record =>
            record.Authority == ConstraintAuthority.TaskContract &&
            record.Origin == ConstraintOrigin.TaskContractYaml &&
            record.Status == ConstraintStatus.Active);
    }

    [Fact]
    public void Project_ApprovalNoneAndNoFlags_HasNoManualRecords()
    {
        var records = ConstraintLedgerProjector.Project(Contract(), "sha256:snapshot").ToList();

        records.Should().NotContain(record =>
            record.Verifiability == ConstraintVerifiability.Manual);
    }

    [Fact]
    public void Project_HumanApprovalOrMigration_ProducesManualRecords()
    {
        var approval = ConstraintLedgerProjector
            .Project(Contract(ApprovalLevel.Human), "sha256:snapshot")
            .ToList();
        approval.Should().Contain(record =>
            record.RequirementKey == "approval.production" &&
            record.Verifiability == ConstraintVerifiability.Manual);

        var migration = ConstraintLedgerProjector
            .Project(Contract(databaseMigration: true), "sha256:snapshot")
            .ToList();
        migration.Should().Contain(record =>
            record.RequirementKey == "constraints.database-migration" &&
            record.Verifiability == ConstraintVerifiability.Manual);
    }

    [Fact]
    public void Build_UnsealedContract_StillProducesSealedRecords()
    {
        var contract = Contract();
        contract.ContractFingerprint.Should().NotBeNull();

        var service = ConstraintLedgerProjector.Build(contract, "sha256:snapshot");

        foreach (var record in service.GetActive())
        {
            ConstraintRecordContract.Validate(record);
        }

        service.ValidateContract(contract).Should().BeEmpty();
    }
}
