using System.Security.Cryptography;
using System.Text;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.ConstraintLedger;

/// <summary>
/// Projects a TaskContract into versioned ConstraintRecords so every execution is
/// bound to an explicit, hashable constraint set (plan §4.3.1).
/// Ids and timestamps are deterministic: the same contract + snapshot always
/// produce the same set hash, so evidence is comparable across runs.
/// </summary>
public static class ConstraintLedgerProjector
{
    /// <summary>Fixed UTC timestamp for projected records so content hashes are reproducible.</summary>
    public static readonly DateTime Epoch = DateTime.UnixEpoch;

    public static ConstraintLedgerService Build(TaskContract contract, string repositorySnapshotId)
    {
        var service = new ConstraintLedgerService();
        Build(contract, repositorySnapshotId, service);
        return service;
    }

    /// <summary>
    /// Projects the contract into an existing ledger service (ingest order:
    /// projected records first so persisted higher-authority records loaded
    /// afterwards can supersede them).
    /// </summary>
    public static void Build(
        TaskContract contract,
        string repositorySnapshotId,
        ConstraintLedgerService service)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(repositorySnapshotId);
        ArgumentNullException.ThrowIfNull(service);

        foreach (var record in Project(contract, repositorySnapshotId))
        {
            service.Ingest(record);
        }

        service.DetectConflicts();
    }

    public static IEnumerable<ConstraintRecord> Project(
        TaskContract contract,
        string repositorySnapshotId)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(repositorySnapshotId);

        var seed = $"{contract.Id}|{contract.ContractFingerprint}|{contract.Objective}|{repositorySnapshotId}";

        if (contract.Scope.Forbidden.Count > 0)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "scope.forbidden",
                $"Forbidden scope patterns: {string.Join(", ", contract.Scope.Forbidden)}",
                string.Join(", ", contract.Scope.Forbidden),
                ConstraintKind.ScopeBoundary,
                ConstraintVerifiability.Deterministic,
                "Scope");
        }

        if (contract.Scope.Allowed.Count > 0)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "scope.allowed",
                $"Changes must stay within allowed patterns: {string.Join(", ", contract.Scope.Allowed)}",
                string.Join(", ", contract.Scope.Allowed),
                ConstraintKind.ScopeBoundary,
                ConstraintVerifiability.Deterministic,
                "Scope");
        }

        yield return Record(
            contract,
            repositorySnapshotId,
            seed,
            "budget.limits",
            $"Budget limits: {contract.Budget.MaxTokens} tokens, " +
            $"{contract.Budget.MaxCostUsd} USD, {contract.Budget.MaxRetries} retries, " +
            $"{contract.Budget.MaxDurationSeconds}s, {contract.Budget.MaxFilesChanged} files",
            "budget",
            ConstraintKind.BudgetLimit,
            ConstraintVerifiability.Deterministic,
            "Budget");

        if (contract.Verification.Build)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "verification.build",
                "Build must succeed",
                "verification",
                ConstraintKind.VerificationRequirement,
                ConstraintVerifiability.Deterministic,
                "Build");
        }

        if (contract.Execution.TestSuites is null)
        {
            if (contract.Verification.UnitTests || contract.Verification.IntegrationTests)
            {
                yield return Record(
                    contract,
                    repositorySnapshotId,
                    seed,
                    "verification.tests",
                    "Required tests must pass",
                    "verification",
                    ConstraintKind.VerificationRequirement,
                    ConstraintVerifiability.Deterministic,
                    "Tests");
            }
        }
        else
        {
            foreach (var suite in contract.Execution.TestSuites.RequiredSuites)
            {
                var name = TestSuiteVerifier.NameFor(suite.Category);
                yield return Record(
                    contract,
                    repositorySnapshotId,
                    seed,
                    $"verification.tests.{suite.Category.ToString().ToLowerInvariant()}",
                    $"Required test suite '{suite.Category}' must pass",
                    "verification",
                    ConstraintKind.VerificationRequirement,
                    ConstraintVerifiability.Deterministic,
                    name);
            }
        }

        if (contract.Verification.SecurityScan)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "verification.security-scan",
                "Security scan must pass",
                "verification",
                ConstraintKind.VerificationRequirement,
                ConstraintVerifiability.Deterministic,
                SecurityScanVerifier.VerifierName);
        }

        if (contract.Verification.Architecture)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "verification.architecture",
                "Architecture verification (EB001) must pass",
                "verification",
                ConstraintKind.VerificationRequirement,
                ConstraintVerifiability.Deterministic,
                "EB001-Architecture");
        }

        if (contract.AcceptanceCriteria.Count > 0 || contract.AcceptanceRequirements.Count > 0)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "acceptance.criteria",
                "All declared acceptance criteria must have passing evidence",
                "acceptance",
                ConstraintKind.AcceptanceCriterion,
                ConstraintVerifiability.Deterministic,
                AcceptanceCriteriaVerifier.Name);
        }

        if (contract.Approval.Production == ApprovalLevel.Human)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "approval.production",
                "Production deployment requires human approval",
                "approval",
                ConstraintKind.ApprovalRequirement,
                ConstraintVerifiability.Manual,
                null);
        }

        if (contract.Constraints.DatabaseMigration)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "constraints.database-migration",
                "Task declares a database migration — manual review required",
                "constraints",
                ConstraintKind.SecurityPolicy,
                ConstraintVerifiability.Manual,
                null);
        }

        if (contract.Constraints.ExternalDependency)
        {
            yield return Record(
                contract,
                repositorySnapshotId,
                seed,
                "constraints.external-dependency",
                "Task declares an external dependency — manual review required",
                "constraints",
                ConstraintKind.SecurityPolicy,
                ConstraintVerifiability.Manual,
                null);
        }
    }

    private static ConstraintRecord Record(
        TaskContract contract,
        string snapshotId,
        string seed,
        string requirementKey,
        string description,
        string scope,
        ConstraintKind kind,
        ConstraintVerifiability verifiability,
        string? verifierName) => new()
        {
            Id = DeterministicId(seed, requirementKey),
            RequirementKey = requirementKey,
            Revision = 1,
            Description = description,
            Scope = scope,
            Kind = kind,
            Status = ConstraintStatus.Active,
            Authority = ConstraintAuthority.TaskContract,
            Origin = ConstraintOrigin.TaskContractYaml,
            Verifiability = verifiability,
            VerifierName = verifierName,
            RepositorySnapshotId = snapshotId,
            ValidFrom = Epoch,
            ValidUntil = null,
            CreatedAt = Epoch
        };

    private static Guid DeterministicId(string seed, string requirementKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{requirementKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
