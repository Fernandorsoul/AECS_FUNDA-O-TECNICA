using AECS.Application.SemanticLinter;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public sealed class EB005Verifier : IVerifier
{
    private readonly EB005HistoricalConflictVerifier _verifier = new();
    private readonly HistoricalDecisionSelection _selection;

    public EB005Verifier(HistoricalDecisionSelection? selection = null)
    {
        _selection = selection ?? new HistoricalDecisionSelection
        {
            Status = HistoricalDecisionSelectionStatus.Unavailable,
            EvaluatedAt = DateTime.UtcNow,
            Message = "Historical decision selection was not supplied."
        };
    }

    public string Name => "EB005-HistoricalConflict";
    public VerificationCategory Category => VerificationCategory.Probabilistic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_selection.Status is HistoricalDecisionSelectionStatus.Unavailable or
                HistoricalDecisionSelectionStatus.Ambiguous)
            {
                return Task.FromResult(Result(
                    context,
                    VerificationStatus.Error,
                    _selection.Status == HistoricalDecisionSelectionStatus.Ambiguous
                        ? Severity.Error
                        : Severity.Warning,
                    _selection.Message,
                    []));
            }
            if (_selection.Status == HistoricalDecisionSelectionStatus.NoHistory)
            {
                return Task.FromResult(Result(
                    context,
                    VerificationStatus.Pass,
                    Severity.Info,
                    _selection.Message,
                    []));
            }

            var input = SemanticAnalysisInput.From(context);
            var result = _verifier.Verify(input, _selection);
            var allConflicts = result.Conflicts.Concat(result.SuppressedConflicts).ToList();
            if (result.HasConflicts)
            {
                var messages = result.Conflicts.Take(5).Select(conflict => conflict.Detail);
                return Task.FromResult(Result(
                    context,
                    result.HasBlockingConflicts
                        ? VerificationStatus.Fail
                        : VerificationStatus.Pass,
                    result.HasBlockingConflicts ? Severity.Error : Severity.Warning,
                    $"Historical conflicts detected ({result.Conflicts.Count}, " +
                    $"suppressed {result.SuppressedConflicts.Count}):\n" +
                    string.Join("\n", messages),
                    allConflicts));
            }

            return Task.FromResult(Result(
                context,
                VerificationStatus.Pass,
                result.SuppressedConflicts.Count > 0 ? Severity.Warning : Severity.Info,
                $"No unsuppressed historical conflicts found " +
                $"({result.SymbolsAnalyzed} symbols, {result.DecisionsChecked} decisions, " +
                $"{result.SuppressedConflicts.Count} suppressed)",
                allConflicts));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result(
                context,
                VerificationStatus.Error,
                Severity.Warning,
                $"EB005 verifier error: {ex.Message}",
                []));
        }
    }

    private VerificationResult Result(
        VerificationContext context,
        VerificationStatus status,
        Severity severity,
        string message,
        IEnumerable<DecisionConflict> conflicts) => new()
        {
            AgentRunId = context.AgentRunId,
            Verifier = Name,
            Status = status,
            Severity = severity,
            Message = message,
            Historical = new HistoricalDecisionVerificationEvidence
            {
                Status = _selection.Status,
                EvaluatedAt = _selection.EvaluatedAt,
                Message = _selection.Message,
                Decisions = _selection.Decisions,
                Suppressions = _selection.Suppressions,
                Conflicts = conflicts.Select(conflict => new HistoricalConflictEvidence
                    {
                        RuleId = conflict.RuleId,
                        DecisionId = conflict.Decision.Id,
                        DecisionVersion = conflict.Decision.Version,
                        Source = conflict.Decision.Source,
                        SourceVersion = conflict.Decision.SourceVersion,
                        SourceHash = conflict.Decision.SourceHash,
                        Authority = conflict.Decision.Authority,
                        SymbolId = conflict.SymbolId,
                        Symbol = conflict.Symbol,
                        FilePath = conflict.FilePath,
                        Severity = conflict.Severity.ToString(),
                        Pattern = $"{conflict.Pattern.Kind}:{conflict.Pattern.Value}",
                        Justification = conflict.Detail,
                        Suppressed = conflict.Suppressed,
                        SuppressionId = conflict.Suppression?.Id ?? string.Empty,
                        SuppressionVersion = conflict.Suppression?.Version
                    })
                    .OrderBy(conflict => conflict.DecisionId, StringComparer.Ordinal)
                    .ThenBy(conflict => conflict.FilePath, StringComparer.Ordinal)
                    .ThenBy(conflict => conflict.SymbolId, StringComparer.Ordinal)
                    .ToList()
            }
        };
}
