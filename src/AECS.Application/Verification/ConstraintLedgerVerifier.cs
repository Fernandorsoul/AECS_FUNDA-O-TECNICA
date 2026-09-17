using AECS.Application.ConstraintLedger;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

/// <summary>
/// Evaluates the active constraint set against the results of the run.
/// Deterministic constraints map to their named verifier: any non-Pass result
/// (or missing result) is a violation and blocks approval. Manual/assisted
/// constraints are reported as PendingReview — never inferred as satisfied
/// (plan §4.1). Unresolved conflicts always block.
/// </summary>
public sealed class ConstraintLedgerVerifier
{
    public const string Name = "ConstraintLedger";

    private readonly ConstraintLedgerService _ledger;
    private readonly ConstraintSetRef _setRef;

    public ConstraintLedgerVerifier(ConstraintLedgerService ledger, ConstraintSetRef setRef)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(setRef);
        _ledger = ledger;
        _setRef = setRef;
    }

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        IReadOnlyList<VerificationResult> priorResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(priorResults);
        cancellationToken.ThrowIfCancellationRequested();

        var active = _ledger.GetActive();
        var unresolvedConflicts = _ledger.AllConflicts.Count(conflict => !conflict.Resolved);
        var assessments = new List<ConstraintAssessment>();

        foreach (var constraint in active)
        {
            assessments.Add(Assess(constraint, priorResults));
        }

        var evidence = new ConstraintLedgerVerificationEvidence
        {
            SetId = _setRef.Id,
            SetRevision = _setRef.Revision,
            SetCanonicalSha256 = _setRef.CanonicalSha256,
            ActiveCount = active.Count,
            UnresolvedConflictCount = unresolvedConflicts,
            Assessments = assessments
        };

        VerificationStatus status;
        Severity severity;
        string message;

        if (evidence.HasViolations)
        {
            var violations = assessments
                .Where(assessment => assessment.Outcome == ConstraintAssessmentOutcome.Violated)
                .Select(assessment => $"{assessment.RequirementKey}: {assessment.Detail}")
                .ToList();
            if (unresolvedConflicts > 0)
            {
                violations.Insert(0, $"{unresolvedConflicts} unresolved constraint conflict(s)");
            }

            status = VerificationStatus.Fail;
            severity = Severity.Critical;
            message = $"Constraint violations ({violations.Count}):\n{string.Join("\n", violations)}";
        }
        else if (evidence.HasPendingReview)
        {
            var pending = assessments
                .Where(assessment => assessment.Outcome == ConstraintAssessmentOutcome.PendingReview)
                .Select(assessment => assessment.RequirementKey)
                .ToList();
            var satisfied = assessments.Count -
                pending.Count;
            status = VerificationStatus.Pass;
            severity = Severity.Warning;
            message =
                $"No violations; {satisfied} constraint(s) satisfied; " +
                $"{pending.Count} pending manual review: {string.Join(", ", pending)}";
        }
        else
        {
            status = VerificationStatus.Pass;
            severity = Severity.Info;
            message = $"All {assessments.Count} active constraint(s) satisfied " +
                $"(set {_setRef.CanonicalSha256})";
        }

        return Task.FromResult(new VerificationResult
        {
            AgentRunId = context.AgentRunId,
            Verifier = Name,
            Status = status,
            Severity = severity,
            Message = message,
            ConstraintLedger = evidence
        });
    }

    private static ConstraintAssessment Assess(
        ConstraintRecord constraint,
        IReadOnlyList<VerificationResult> priorResults)
    {
        switch (constraint.Verifiability)
        {
            case ConstraintVerifiability.Deterministic:
                return AssessDeterministic(constraint, priorResults);
            case ConstraintVerifiability.Manual:
            case ConstraintVerifiability.Assisted:
            case ConstraintVerifiability.ProcessInvariant:
            default:
                return new ConstraintAssessment
                {
                    RequirementKey = constraint.RequirementKey,
                    Revision = constraint.Revision,
                    Kind = constraint.Kind,
                    Verifiability = constraint.Verifiability,
                    VerifierName = constraint.VerifierName,
                    Outcome = ConstraintAssessmentOutcome.PendingReview,
                    Detail = $"Requires {constraint.Verifiability} review — not automatically verified"
                };
        }
    }

    private static ConstraintAssessment AssessDeterministic(
        ConstraintRecord constraint,
        IReadOnlyList<VerificationResult> priorResults)
    {
        if (string.IsNullOrWhiteSpace(constraint.VerifierName))
        {
            return Violated(
                constraint,
                "deterministic constraint has no verifier assigned");
        }

        var match = priorResults.FirstOrDefault(result => string.Equals(
            result.Verifier,
            constraint.VerifierName,
            StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return Violated(
                constraint,
                $"required verifier '{constraint.VerifierName}' produced no result");
        }

        if (match.Status != VerificationStatus.Pass)
        {
            return Violated(
                constraint,
                $"verifier '{constraint.VerifierName}' status {match.Status}: {match.Message}");
        }

        return new ConstraintAssessment
        {
            RequirementKey = constraint.RequirementKey,
            Revision = constraint.Revision,
            Kind = constraint.Kind,
            Verifiability = constraint.Verifiability,
            VerifierName = constraint.VerifierName,
            Outcome = ConstraintAssessmentOutcome.Satisfied,
            Detail = $"verified by '{constraint.VerifierName}'"
        };
    }

    private static ConstraintAssessment Violated(ConstraintRecord constraint, string detail) => new()
    {
        RequirementKey = constraint.RequirementKey,
        Revision = constraint.Revision,
        Kind = constraint.Kind,
        Verifiability = constraint.Verifiability,
        VerifierName = constraint.VerifierName,
        Outcome = ConstraintAssessmentOutcome.Violated,
        Detail = detail
    };
}
