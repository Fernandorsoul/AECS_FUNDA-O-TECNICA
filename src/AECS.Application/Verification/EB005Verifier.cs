using AECS.Application.SemanticLinter;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class EB005Verifier : IVerifier
{
    private readonly EB005HistoricalConflictVerifier _verifier = new();
    private readonly List<HistoricalDecision> _decisions;

    public EB005Verifier(List<HistoricalDecision>? decisions = null)
    {
        _decisions = decisions ?? GetDefaultDecisions();
    }

    public string Name => "EB005-HistoricalConflict";
    public VerificationCategory Category => VerificationCategory.Probabilistic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var result = _verifier.Verify(context.RepoPath, _decisions);

            if (result.HasConflicts)
            {
                var messages = result.Conflicts
                    .Take(5)
                    .Select(c => c.Detail);

                var hasErrors = result.Conflicts.Any(c => c.Severity >= RuleSeverity.Error);

                return Task.FromResult(new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = hasErrors ? VerificationStatus.Fail : VerificationStatus.Pass,
                    Severity = hasErrors ? Severity.Error : Severity.Warning,
                    Message = $"Historical conflicts detected ({result.Conflicts.Count}):\n{string.Join("\n", messages)}"
                });
            }

            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No historical conflicts found ({result.FilesAnalyzed} files, {result.DecisionsChecked} decisions)"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Error,
                Severity = Severity.Warning,
                Message = $"EB005 verifier error: {ex.Message}"
            });
        }
    }

    private static List<HistoricalDecision> GetDefaultDecisions()
    {
        return
        [
            new HistoricalDecision
            {
                Id = "ADR-001",
                Source = "ADR-001",
                Type = DecisionType.Adr,
                Description = "Probabilistic Discovery, Deterministic Enforcement — critical rules must be deterministic",
                ProhibitedPatterns = ["LLMOnly", "AIDecision", "ProbabilisticOnly"]
            },
            new HistoricalDecision
            {
                Id = "ADR-006",
                Source = "ADR-006",
                Type = DecisionType.Adr,
                Description = "Verification Before Adaptive Learning — no ML until verification is stable",
                ProhibitedPatterns = ["ReinforcementLearning", "NeuralNetwork", "DeepLearning"]
            }
        ];
    }
}
