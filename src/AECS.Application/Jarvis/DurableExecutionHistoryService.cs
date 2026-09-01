using AECS.Application.EvidenceGraph;
using AECS.Domain.Enums;
using AECS.Domain.Exceptions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Jarvis;

public sealed class DurableExecutionHistoryService
{
    private readonly IExecutionEvidenceStore _store;
    private readonly EvidenceGraphService _graphs;
    private readonly EvidenceReadScope _scope;

    public DurableExecutionHistoryService(
        IExecutionEvidenceStore store,
        IEvidenceGraphSource graphSource,
        string repositoryPath,
        string principal)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(graphSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        _store = store;
        _graphs = new EvidenceGraphService(graphSource);
        _scope = new EvidenceReadScope
        {
            RepositoryPath = Path.GetFullPath(repositoryPath),
            Principal = principal.Trim()
        };
    }

    public Task<DurableHistoryResult> StatusAsync(CancellationToken cancellationToken) =>
        QueryAsync(new DurableHistoryQuery { Limit = 1 }, cancellationToken);

    public async Task<DurableHistoryResult> QueryAsync(
        DurableHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(query, cancellationToken);
        return new DurableHistoryResult
        {
            Items = loaded.Items
                .Select(item => CreateSummary(item.Summary, item.Evidence))
                .ToList(),
            Diagnostics = loaded.Diagnostics
        };
    }

    public async Task<DurableExplanationResult> ExplainAsync(
        DurableHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var single = new DurableHistoryQuery
        {
            EvidenceId = query.EvidenceId,
            TaskId = query.TaskId,
            RunId = query.RunId,
            CandidateId = query.CandidateId,
            Limit = 1
        };
        var loaded = await LoadAsync(single, cancellationToken);
        if (loaded.Items.Count == 0)
        {
            return new DurableExplanationResult
            {
                Diagnostics = loaded.Diagnostics.Count > 0
                    ? loaded.Diagnostics
                    : ["No authenticated evidence matched the requested execution."]
            };
        }
        var item = loaded.Items[0];
        var explanation = CreateExplanation(item.Summary, item.Evidence);
        explanation.Diagnostics.AddRange(loaded.Diagnostics);
        return new DurableExplanationResult
        {
            Item = explanation,
            Diagnostics = loaded.Diagnostics
        };
    }

    public async Task<DurableContextResult> ContextAsync(
        DurableHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var explanation = await ExplainAsync(query, cancellationToken);
        return new DurableContextResult
        {
            Execution = explanation.Item?.Execution,
            Context = explanation.Item?.Persisted.Context,
            Diagnostics = explanation.Item?.Diagnostics
                .Concat(explanation.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? explanation.Diagnostics
        };
    }

    private async Task<LoadedHistory> LoadAsync(
        DurableHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query), "History limit must be 1-500.");
        if (query.EvidenceId == Guid.Empty || query.RunId == Guid.Empty ||
            query.CandidateId == Guid.Empty)
        {
            throw new ArgumentException("Execution lookup GUIDs must be non-empty.", nameof(query));
        }
        _store.EnsureRepositoryIsolation(_scope.RepositoryPath);
        var summaries = new List<EvidenceGraphSummary>();
        var diagnostics = new List<string>();
        if (query.EvidenceId.HasValue)
        {
            try
            {
                var graph = await _graphs.ShowAsync(
                    query.EvidenceId.Value,
                    _scope,
                    cancellationToken);
                if (graph is not null)
                {
                    summaries.Add(graph.Summary);
                    diagnostics.AddRange(graph.Diagnostics);
                }
            }
            catch (EvidenceIntegrityException)
            {
                diagnostics.Add("The requested evidence was invalid and was omitted.");
            }
            catch (IOException)
            {
                diagnostics.Add("The requested evidence could not be read and was omitted.");
            }
        }
        else
        {
            var result = await _graphs.ListAsync(new EvidenceGraphQuery
            {
                TaskId = NullIfWhiteSpace(query.TaskId),
                RunId = query.RunId,
                CandidateId = query.CandidateId,
                Limit = query.Limit
            }, _scope, cancellationToken);
            summaries.AddRange(result.Items);
            diagnostics.AddRange(result.Diagnostics);
        }

        var items = new List<LoadedExecution>();
        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var evidence = await _store.LoadAsync(
                    summary.EvidenceId,
                    cancellationToken);
                if (evidence is null)
                {
                    diagnostics.Add(
                        $"Evidence {summary.EvidenceId:N} disappeared after authentication and was omitted.");
                    continue;
                }
                items.Add(new LoadedExecution(summary, evidence));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                diagnostics.Add(
                    $"Evidence {summary.EvidenceId:N} changed after authentication and was omitted.");
            }
        }
        return new LoadedHistory(items, diagnostics);
    }

    private static DurableExecutionSummary CreateSummary(
        EvidenceGraphSummary summary,
        ExecutionEvidence evidence)
    {
        var status = EvidenceStatus(summary, evidence);
        return new DurableExecutionSummary
        {
            Status = status,
            EvidenceId = summary.EvidenceId,
            EvidenceSchemaVersion = summary.SchemaVersion,
            Authority = summary.Authority,
            EvidenceHash = summary.EvidenceHash,
            TaskId = evidence.TaskContract.Id,
            Objective = evidence.TaskContract.Objective,
            RunId = evidence.AgentRun.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            BaselineCommit = evidence.Baseline.Commit,
            DiffHash = evidence.CandidateChangeSet.DiffHash,
            Risk = evidence.TaskContract.Constraints.SecurityRisk,
            Model = evidence.AgentRun.Model,
            Decision = evidence.FinalDecision.Decision,
            State = evidence.FinalDecision.State,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt
        };
    }

    private static DurableExecutionExplanation CreateExplanation(
        EvidenceGraphSummary summary,
        ExecutionEvidence evidence)
    {
        var execution = CreateSummary(summary, evidence);
        var gates = evidence.BaselineVerificationResults
            .Select(result => Gate("baseline", result))
            .Concat(evidence.VerificationResults.Select(result => Gate("candidate", result)))
            .ToList();
        var required = evidence.FinalDecision.RequiredVerifiers
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = gates.Select(gate => gate.Verifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextStatus = ContextStatus(evidence.ContextManifest);
        var diagnostics = new List<string>();
        if (execution.Status == JarvisEvidenceStatus.Partial)
        {
            diagnostics.Add(
                "The authenticated execution is incomplete; unavailable facts were left empty.");
        }
        if (contextStatus == JarvisEvidenceStatus.Partial)
            diagnostics.Add("The authenticated context manifest is missing or incomplete.");
        var usage = evidence.AgentResult.UsageAccounting;
        return new DurableExecutionExplanation
        {
            Execution = execution,
            Persisted = new DurablePersistedFacts
            {
                DecisionReason = evidence.FinalDecision.Reason,
                DecidedAt = evidence.FinalDecision.DecidedAt,
                RequiredVerifiers = evidence.FinalDecision.RequiredVerifiers.ToList(),
                Gates = gates,
                AcceptanceCriteria = evidence.AcceptanceCriteriaResults.Select(item =>
                    new DurableAcceptanceFact
                    {
                        CriterionId = item.CriterionId,
                        Description = item.Description,
                        Status = item.Status,
                        EvidenceType = item.EvidenceType.ToString(),
                        EvidenceReference = item.EvidenceReference,
                        EvidenceReferences = item.EvidenceReferences.ToList()
                    }).ToList(),
                Attempts = evidence.AgentAttempts.Select(item => new DurableAttemptFact
                {
                    Id = item.Id,
                    Number = item.AttemptNumber,
                    StartedAt = item.StartedAt,
                    FinishedAt = item.FinishedAt,
                    Success = item.Success,
                    FailureKind = item.FailureKind.ToString(),
                    WillRetry = item.WillRetry,
                    Reason = item.DecisionReason
                }).ToList(),
                Budget = new DurableBudgetFact
                {
                    AttemptsUsed = evidence.BudgetUsage.AttemptsUsed,
                    MaximumAttempts = evidence.BudgetUsage.MaximumAttempts,
                    InputTokens = evidence.BudgetUsage.InputTokens,
                    OutputTokens = evidence.BudgetUsage.OutputTokens,
                    EstimatedCostUsd = evidence.BudgetUsage.EstimatedCost,
                    AccountedCostUsd = usage?.AccountedCostUsd,
                    AccountedCostBasis = usage?.AccountedCostBasis ?? "unavailable",
                    WallClockSeconds = evidence.BudgetUsage.WallClockElapsed.TotalSeconds,
                    WallClockLimitSeconds = evidence.BudgetUsage.WallClockLimitSeconds,
                    Exhausted = evidence.BudgetUsage.Exhausted,
                    ExhaustionReason = evidence.BudgetUsage.ExhaustionReason
                },
                Context = Context(evidence.ContextManifest, contextStatus),
                Promotions = evidence.Promotions.Select(item => new DurablePromotionFact
                {
                    Id = item.Id,
                    Action = item.Action.ToString(),
                    Status = item.Status.ToString(),
                    Actor = item.Actor,
                    ApprovalReference = item.ApprovalReference,
                    ReviewDecision = item.ReviewDecision?.ToString() ?? string.Empty,
                    Justification = item.Justification ?? string.Empty,
                    ValidUntil = item.ValidUntil,
                    PolicyReference = item.PolicyReference ?? string.Empty,
                    StartedAt = item.StartedAt,
                    FinishedAt = item.FinishedAt
                }).ToList()
            },
            Derived = new DurableDerivedFacts
            {
                TotalTokens = evidence.BudgetUsage.InputTokens +
                              evidence.BudgetUsage.OutputTokens,
                ChangedFiles = evidence.CandidateChangeSet.ChangedFiles.Count,
                PassedGates = gates.Count(gate => gate.Status == VerificationStatus.Pass),
                FailedGates = gates.Count(gate => gate.Status is
                    VerificationStatus.Fail or VerificationStatus.Error),
                MissingRequiredGates = required.Count(name => !present.Contains(name))
            },
            Interpretation = new DurableInterpretation
            {
                Status = execution.Status,
                Summary = execution.Status == JarvisEvidenceStatus.Complete
                    ? $"Persisted decision {execution.Decision}: {evidence.FinalDecision.Reason}"
                    : "Authenticated evidence is partial; no complete explanation is available."
            },
            Diagnostics = diagnostics
        };
    }

    private static DurableGateFact Gate(string phase, VerificationResult result) => new()
    {
        Id = result.Id,
        Phase = phase,
        Verifier = result.Verifier,
        Status = result.Status,
        Message = result.Message,
        CreatedAt = result.CreatedAt
    };

    private static DurableContextFact Context(
        ContextManifest context,
        JarvisEvidenceStatus status) => new()
    {
        Status = status,
        Id = context.Id,
        SchemaVersion = context.SchemaVersion,
        StrategyVersion = context.StrategyVersion,
        Strategy = context.Strategy,
        ManifestHash = context.ManifestHash,
        BaselineCommit = context.BaselineCommit,
        Model = context.Model,
        Tokenizer = context.Tokenizer,
        EstimatedTokens = context.EstimatedTokens,
        MaximumTokens = context.MaxTokens,
        Files = context.Files.Select(file => new DurableContextFileFact
        {
            Path = file.Path,
            Sha256 = file.Sha256,
            IncludedSha256 = file.IncludedSha256,
            IncludedTokens = file.IncludedTokens,
            Truncated = file.Truncated,
            Rank = file.Rank,
            Reasons = file.Reasons.ToList()
        }).ToList()
    };

    private static JarvisEvidenceStatus EvidenceStatus(
        EvidenceGraphSummary summary,
        ExecutionEvidence evidence) =>
        summary.EvidenceId == Guid.Empty ||
        string.IsNullOrWhiteSpace(summary.SchemaVersion) ||
        string.IsNullOrWhiteSpace(summary.Authority) ||
        string.IsNullOrWhiteSpace(summary.EvidenceHash) ||
        string.IsNullOrWhiteSpace(evidence.TaskContract.Id) ||
        evidence.AgentRun.Id == Guid.Empty ||
        string.IsNullOrWhiteSpace(evidence.AgentRun.Model) ||
        evidence.CandidateChangeSet.Id == Guid.Empty ||
        string.IsNullOrWhiteSpace(evidence.Baseline.Commit) ||
        string.IsNullOrWhiteSpace(evidence.CandidateChangeSet.DiffHash) ||
        string.IsNullOrWhiteSpace(evidence.FinalDecision.Reason) ||
        ContextStatus(evidence.ContextManifest) == JarvisEvidenceStatus.Partial
            ? JarvisEvidenceStatus.Partial
            : JarvisEvidenceStatus.Complete;

    private static JarvisEvidenceStatus ContextStatus(ContextManifest context) =>
        string.IsNullOrWhiteSpace(context.Id) ||
        string.IsNullOrWhiteSpace(context.SchemaVersion) ||
        string.IsNullOrWhiteSpace(context.Strategy) ||
        string.IsNullOrWhiteSpace(context.ManifestHash)
            ? JarvisEvidenceStatus.Partial
            : JarvisEvidenceStatus.Complete;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record LoadedExecution(
        EvidenceGraphSummary Summary,
        ExecutionEvidence Evidence);

    private sealed record LoadedHistory(
        List<LoadedExecution> Items,
        List<string> Diagnostics);
}
