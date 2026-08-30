using AECS.Application.Classification;
using AECS.Application.ContextCompiler;
using AECS.Application.Execution;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Staging;

public sealed class StagedExecutionResult
{
    public TaskContract Contract { get; init; } = new();
    public RiskLevel Risk { get; init; }
    public string Model { get; init; } = string.Empty;
    public AgentRun AgentRun { get; init; } = new();
    public AgentRunResult AgentResult { get; init; } = new();
    public IReadOnlyList<AgentAttemptEvidence> AgentAttempts { get; init; } = [];
    public ExecutionBudgetEvidence BudgetUsage { get; init; } = new();
    public BaselineSnapshot Baseline { get; init; } = new();
    public CandidateChangeSet CandidateChangeSet { get; init; } = new();
    public IReadOnlyList<VerificationResult> BaselineVerificationResults { get; init; } = [];
    public IReadOnlyList<ExecutionCommandEvidence> BaselineCommands { get; init; } = [];
    public ContextManifest ContextManifest { get; init; } = new();
    public IReadOnlyList<VerificationResult> VerificationResults { get; init; } = [];
    public IReadOnlyList<AcceptanceCriterionResult> AcceptanceCriteriaResults { get; init; } = [];
    public IReadOnlyList<ExecutionCommandEvidence> CandidateCommands { get; init; } = [];
    public DecisionResult Decision { get; init; } = new();
    public TaskState FinalState { get; init; }
    public Guid EvidenceId { get; init; }
    public string EvidenceLocation { get; init; } = string.Empty;
    public bool OriginalRepositoryUnchanged { get; init; }
}

public sealed class StagedExecutionPipeline
{
    private readonly IAgentAdapter _agentAdapter;
    private readonly IProcessRunner _processRunner;
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly GitWorkspaceManager _workspaceManager;
    private readonly RepositoryContextCompiler _contextCompiler;
    private readonly RiskClassifier _riskClassifier = new();
    private readonly ExecutionController _executionController = new();
    private readonly DecisionEngine _decisionEngine = new();
    private readonly FileApplicator _fileApplicator = new();
    private readonly AgentExecutionCoordinator _agentExecutionCoordinator;

    public StagedExecutionPipeline(
        IAgentAdapter agentAdapter,
        IProcessRunner processRunner,
        IExecutionEvidenceStore evidenceStore,
        RepositoryContextCompiler? contextCompiler = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        TimeSpan? maximumRetryBackoff = null)
    {
        _agentAdapter = agentAdapter;
        _processRunner = processRunner;
        _evidenceStore = evidenceStore;
        _workspaceManager = new GitWorkspaceManager(processRunner);
        _contextCompiler = contextCompiler ?? new RepositoryContextCompiler();
        _agentExecutionCoordinator = new AgentExecutionCoordinator(
            agentAdapter,
            retryDelay,
            maximumRetryBackoff);
    }

    public async Task<StagedExecutionResult> RunAsync(
        string repositoryPath,
        TaskContract inputContract,
        CancellationToken cancellationToken)
    {
        using var budgetScope = new ExecutionBudgetScope(
            inputContract.Budget,
            cancellationToken);
        var stateMachine = new TaskStateMachine();
        stateMachine.TransitionTo(TaskState.ContractReady);

        var risk = _riskClassifier.Classify(inputContract);
        var contract = WithRisk(inputContract, risk);
        var plan = await _executionController.PlanAsync(contract, budgetScope.Token);
        stateMachine.TransitionTo(TaskState.Planned);

        var baseline = await _workspaceManager.CaptureBaselineAsync(
            repositoryPath,
            budgetScope.Token);
        _evidenceStore.EnsureRepositoryIsolation(baseline.RepositoryPath);

        var agentRunId = Guid.NewGuid().ToString("N");
        var startedAt = DateTime.UtcNow;
        stateMachine.TransitionTo(TaskState.BaselineVerifying);

        var baselineCommands = new List<ExecutionCommandEvidence>();
        List<VerificationResult> baselineVerificationResults;
        await using (var preflightWorkspace = await _workspaceManager.CreateWorkspaceAsync(
            baseline,
            budgetScope.Token))
        {
            baselineVerificationResults = await VerifyBaselineAsync(
                new VerificationContext
                {
                    TaskId = contract.Id,
                    AgentRunId = agentRunId,
                    RepoPath = preflightWorkspace.Path,
                    Contract = contract,
                    CommandEvidence = baselineCommands
                },
                budgetScope.Token,
                () => budgetScope.RemainingDuration);
        }

        await _workspaceManager.EnsureBaselineUnchangedAsync(
            baseline,
            CancellationToken.None);

        var baselineFailures = baselineVerificationResults
            .Where(result => result.Status != VerificationStatus.Pass)
            .Select(result => $"{result.Verifier}: {result.Status} - {result.Message}")
            .ToList();
        if (baselineFailures.Count > 0)
        {
            var baselineAgentResult = new AgentRunResult
            {
                Success = false,
                StdErr = $"Baseline preflight failed: {string.Join("; ", baselineFailures)}",
                ExitCode = -1,
                ExitReason = "BaselinePreflightFailed"
            };
            var baselineDecision = ApplyTerminalExecutionState(new DecisionResult
            {
                Decision = TaskDecision.Rejected,
                TargetState = TaskState.Rejected,
                Reason = $"Baseline preflight failed: {string.Join("; ", baselineFailures)}",
                Failures = baselineFailures
            }, baselineAgentResult, budgetScope, string.Empty);
            stateMachine.TransitionTo(baselineDecision.TargetState);

            return await PublishAsync(
                contract,
                risk,
                plan.Model,
                baseline,
                agentRunId,
                startedAt,
                baselineAgentResult,
                [],
                string.Empty,
                EmptyCandidate(contract.Id, agentRunId, baseline.Commit),
                baselineVerificationResults,
                baselineCommands,
                RepositoryContextCompiler.EmptyManifest(contract.Id, baseline.Commit),
                [],
                [],
                [],
                baselineDecision,
                stateMachine,
                budgetScope);
        }

        stateMachine.TransitionTo(TaskState.Running);
        AgentRunResult agentResult;
        List<AgentAttemptEvidence> agentAttempts;
        string budgetExhaustionReason;
        ContextManifest contextManifest;
        CandidateChangeSet candidate;
        List<VerificationResult> verificationResults;
        var acceptanceCriteriaResults = new List<AcceptanceCriterionResult>();
        DecisionResult decision;
        var candidateCommands = new List<ExecutionCommandEvidence>();

        await using (var workspace = await _workspaceManager.CreateWorkspaceAsync(
            baseline,
            budgetScope.Token))
        {
            var compiledContext = _contextCompiler.Compile(
                workspace.Path,
                contract,
                baseline.Commit);
            contextManifest = compiledContext.Manifest;

            var agentOutcome = await _agentExecutionCoordinator.ExecuteAsync(
                new AgentExecutionRequest
                {
                    TaskId = contract.Id,
                    Objective = contract.Objective,
                    AcceptanceCriteria = contract.AcceptanceCriteria,
                    RepoPath = workspace.Path,
                    Scope = contract.Scope,
                    Budget = contract.Budget,
                    Risk = risk,
                    Model = plan.Model,
                    CodeContext = compiledContext.CodeContext,
                    ContextPrompt = compiledContext.Prompt
                }, budgetScope);
            agentResult = agentOutcome.Result;
            agentAttempts = agentOutcome.Attempts;
            budgetExhaustionReason = agentOutcome.BudgetExhaustionReason;

            var applicationResult = agentResult.Success
                ? _fileApplicator.ApplyChanges(agentResult.StdOut, workspace.Path)
                : new FileApplicatorResult
                {
                    Success = false,
                    Errors = ["Agent failed; its response was not applied"]
                };

            candidate = await _workspaceManager.CreateCandidateAsync(
                workspace,
                contract.Id,
                agentRunId,
                agentResult.FailureKind is AgentFailureKind.Cancelled or AgentFailureKind.BudgetExceeded
                    ? CancellationToken.None
                    : budgetScope.Token);
            stateMachine.TransitionTo(TaskState.CandidateProduced);
            stateMachine.TransitionTo(TaskState.Verifying);

            var verificationContext = new VerificationContext
            {
                TaskId = contract.Id,
                AgentRunId = agentRunId,
                RepoPath = workspace.Path,
                Contract = contract,
                AgentResult = agentResult,
                CandidateChangeSet = candidate,
                CommandEvidence = candidateCommands
            };

            verificationResults = await VerifyAsync(
                verificationContext,
                applicationResult,
                acceptanceCriteriaResults,
                agentResult.FailureKind is AgentFailureKind.Cancelled or AgentFailureKind.BudgetExceeded
                    ? CancellationToken.None
                    : budgetScope.Token,
                () => budgetScope.RemainingDuration);
            decision = _decisionEngine.Decide(verificationResults, contract);
            decision = ApplyTerminalExecutionState(
                decision,
                agentResult,
                budgetScope,
                budgetExhaustionReason);
            stateMachine.TransitionTo(decision.TargetState);
        }

        return await PublishAsync(
            contract,
            risk,
            plan.Model,
            baseline,
            agentRunId,
            startedAt,
            agentResult,
            agentAttempts,
            budgetExhaustionReason,
            candidate,
            baselineVerificationResults,
            baselineCommands,
            contextManifest,
            verificationResults,
            acceptanceCriteriaResults,
            candidateCommands,
            decision,
            stateMachine,
            budgetScope);
    }

    private async Task<StagedExecutionResult> PublishAsync(
        TaskContract contract,
        RiskLevel risk,
        string model,
        BaselineSnapshot baseline,
        string agentRunId,
        DateTime startedAt,
        AgentRunResult agentResult,
        List<AgentAttemptEvidence> agentAttempts,
        string budgetExhaustionReason,
        CandidateChangeSet candidate,
        List<VerificationResult> baselineVerificationResults,
        List<ExecutionCommandEvidence> baselineCommands,
        ContextManifest contextManifest,
        List<VerificationResult> verificationResults,
        List<AcceptanceCriterionResult> acceptanceCriteriaResults,
        List<ExecutionCommandEvidence> candidateCommands,
        DecisionResult decision,
        TaskStateMachine stateMachine,
        ExecutionBudgetScope budgetScope)
    {
        await _workspaceManager.EnsureBaselineUnchangedAsync(
            baseline,
            CancellationToken.None);

        var agentRun = new AgentRun
        {
            Id = Guid.Parse(agentRunId),
            TaskId = contract.Id,
            AgentType = _agentAdapter.GetType().Name,
            Provider = _agentAdapter.GetType().Namespace ?? string.Empty,
            Model = model,
            StartedAt = agentAttempts.Count > 0 ? agentAttempts[0].StartedAt : startedAt,
            FinishedAt = agentAttempts.Count > 0 ? agentAttempts[^1].FinishedAt : startedAt,
            InputTokens = agentResult.InputTokens,
            OutputTokens = agentResult.OutputTokens,
            EstimatedCost = agentResult.EstimatedCost,
            RetryCount = Math.Max(0, agentAttempts.Count - 1),
            ExitReason = agentResult.ExitReason,
            FilesChanged = agentResult.FilesChanged.ToList()
        };

        var baselineFailed = baselineVerificationResults.Any(
            result => result.Status != VerificationStatus.Pass);
        var evidence = new ExecutionEvidence
        {
            TaskContract = contract,
            AgentRun = agentRun,
            AgentResult = agentResult,
            AgentAttempts = agentAttempts,
            BudgetUsage = CreateBudgetEvidence(
                contract.Budget,
                budgetScope,
                agentResult,
                agentAttempts,
                budgetExhaustionReason),
            Baseline = baseline,
            BaselineVerificationResults = baselineVerificationResults,
            BaselineCommands = baselineCommands,
            ContextManifest = contextManifest,
            CandidateChangeSet = candidate,
            VerificationResults = verificationResults,
            AcceptanceCriteriaResults = acceptanceCriteriaResults,
            CandidateCommands = candidateCommands,
            FinalDecision = new FinalDecisionRecord
            {
                Decision = decision.Decision,
                State = decision.TargetState,
                Reason = decision.Reason,
                RequiredVerifiers = (baselineFailed
                        ? GetRequiredBaselineVerifiers(contract)
                        : DecisionEngine.GetRequiredVerifiers(contract))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DecidedAt = DateTime.UtcNow
            },
            StateTransitions = stateMachine.History
                .Select(item => $"{item.From}->{item.To}@{item.At:O}")
                .ToList()
        };

        var evidenceLocation = await _evidenceStore.SaveAsync(evidence, CancellationToken.None);
        await _workspaceManager.EnsureBaselineUnchangedAsync(
            baseline,
            CancellationToken.None);

        return new StagedExecutionResult
        {
            Contract = contract,
            Risk = risk,
            Model = model,
            AgentRun = agentRun,
            AgentResult = agentResult,
            AgentAttempts = agentAttempts,
            BudgetUsage = evidence.BudgetUsage,
            Baseline = baseline,
            CandidateChangeSet = candidate,
            BaselineVerificationResults = baselineVerificationResults,
            BaselineCommands = baselineCommands,
            ContextManifest = contextManifest,
            VerificationResults = verificationResults,
            AcceptanceCriteriaResults = acceptanceCriteriaResults,
            CandidateCommands = candidateCommands,
            Decision = decision,
            FinalState = stateMachine.CurrentState,
            EvidenceId = evidence.Id,
            EvidenceLocation = evidenceLocation,
            OriginalRepositoryUnchanged = true
        };
    }

    private async Task<List<VerificationResult>> VerifyBaselineAsync(
        VerificationContext context,
        CancellationToken cancellationToken,
        Func<TimeSpan> remainingDuration)
    {
        var results = new List<VerificationResult>();

        if (context.Contract.Verification.Build)
        {
            results.Add(await RunVerifierAsync(
                new BuildVerifier(_processRunner, remainingDuration),
                context,
                cancellationToken));
        }

        var buildPassed = results
            .Where(result => result.Verifier == "Build")
            .All(result => result.Status == VerificationStatus.Pass);
        if (context.Contract.Verification.UnitTests ||
            context.Contract.Verification.IntegrationTests)
        {
            results.Add(buildPassed
                ? await RunVerifierAsync(
                    new TestVerifier(_processRunner, remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Tests", "Baseline build prerequisite failed"));
        }

        return results;
    }

    private static IReadOnlySet<string> GetRequiredBaselineVerifiers(TaskContract contract)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (contract.Verification.Build)
            required.Add("Build");
        if (contract.Verification.UnitTests || contract.Verification.IntegrationTests)
            required.Add("Tests");
        return required;
    }

    private static CandidateChangeSet EmptyCandidate(
        string taskId,
        string agentRunId,
        string baselineCommit) => new()
        {
            TaskId = taskId,
            AgentRunId = agentRunId,
            BaselineCommit = baselineCommit,
            DiffHash = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        };

    private async Task<List<VerificationResult>> VerifyAsync(
        VerificationContext context,
        FileApplicatorResult applicationResult,
        List<AcceptanceCriterionResult> acceptanceCriteriaResults,
        CancellationToken cancellationToken,
        Func<TimeSpan> remainingDuration)
    {
        var results = new List<VerificationResult>();
        var prerequisites = new IVerifier[]
        {
            new AgentSuccessVerifier(),
            new FileApplicationVerifier(applicationResult),
            new CandidateChangeVerifier(),
            new ScopeVerifier(),
            new BudgetVerifier()
        };

        foreach (var verifier in prerequisites)
            results.Add(await RunVerifierAsync(verifier, context, cancellationToken));

        var prerequisitesPassed = results.All(result => result.Status == VerificationStatus.Pass);

        if (context.Contract.Verification.Build)
        {
            results.Add(prerequisitesPassed
                ? await RunVerifierAsync(
                    new BuildVerifier(_processRunner, remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Build", "Trust-boundary prerequisite failed"));
        }

        var buildPassed = results
            .Where(result => result.Verifier == "Build")
            .All(result => result.Status == VerificationStatus.Pass);

        if (context.Contract.Verification.UnitTests || context.Contract.Verification.IntegrationTests)
        {
            results.Add(prerequisitesPassed && buildPassed
                ? await RunVerifierAsync(
                    new TestVerifier(_processRunner, remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Tests", "Trust-boundary or build prerequisite failed"));
        }

        if (prerequisitesPassed && buildPassed)
        {
            var semanticVerifiers = new IVerifier[]
            {
                new EB001Verifier(),
                new EB002Verifier(),
                new EB003Verifier(),
                new EB004Verifier(),
                new EB005Verifier()
            };

            foreach (var verifier in semanticVerifiers)
                results.Add(await RunVerifierAsync(verifier, context, cancellationToken));
        }

        if (AcceptanceCriteriaVerifier.GetEffectiveCriteria(context.Contract).Count > 0)
        {
            var acceptance = await new AcceptanceCriteriaVerifier(
                _processRunner,
                remainingDuration).VerifyAsync(
                context,
                results,
                prerequisitesPassed && buildPassed,
                cancellationToken);
            acceptanceCriteriaResults.AddRange(acceptance.Criteria);
            results.Add(acceptance.AggregateResult);
        }

        return results;
    }

    private static async Task<VerificationResult> RunVerifierAsync(
        IVerifier verifier,
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await verifier.VerifyAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = verifier.Name,
                Status = VerificationStatus.Error,
                Severity = Severity.Critical,
                Message = $"Verifier threw an exception: {ex.Message}"
            };
        }
    }

    private static VerificationResult Skipped(
        string agentRunId,
        string verifier,
        string reason) => new()
        {
            AgentRunId = agentRunId,
            Verifier = verifier,
            Status = VerificationStatus.Skip,
            Severity = Severity.Warning,
            Message = reason
        };

    private static DecisionResult ApplyTerminalExecutionState(
        DecisionResult decision,
        AgentRunResult agentResult,
        ExecutionBudgetScope budgetScope,
        string budgetExhaustionReason)
    {
        TaskState? targetState = null;
        string? reason = null;
        if (budgetScope.CallerCancellationRequested ||
            agentResult.FailureKind == AgentFailureKind.Cancelled)
        {
            targetState = TaskState.Cancelled;
            reason = "Execution cancelled; no further attempts or commands were allowed";
        }
        else if (budgetScope.WallClockExhausted)
        {
            targetState = TaskState.TimedOut;
            reason = "Shared wall-clock budget exhausted; active process tree was terminated";
        }
        else if (agentResult.FailureKind == AgentFailureKind.BudgetExceeded)
        {
            targetState = TaskState.BudgetExceeded;
            reason = string.IsNullOrWhiteSpace(budgetExhaustionReason)
                ? agentResult.StdErr
                : budgetExhaustionReason;
        }
        else if (!agentResult.Success &&
                 !string.Equals(
                     agentResult.ExitReason,
                     "BaselinePreflightFailed",
                     StringComparison.Ordinal))
        {
            targetState = TaskState.AgentFailed;
            reason = $"Agent failed without a permitted retry: {agentResult.ExitReason} - {agentResult.StdErr}";
        }

        if (targetState is null)
            return decision;

        var terminalFailure = reason ?? targetState.Value.ToString();
        return new DecisionResult
        {
            Decision = TaskDecision.Rejected,
            TargetState = targetState.Value,
            Reason = terminalFailure,
            Failures = [.. decision.Failures, terminalFailure]
        };
    }

    private static ExecutionBudgetEvidence CreateBudgetEvidence(
        ExecutionBudget budget,
        ExecutionBudgetScope budgetScope,
        AgentRunResult agentResult,
        IReadOnlyCollection<AgentAttemptEvidence> attempts,
        string budgetExhaustionReason)
    {
        var exhaustionReason = budgetExhaustionReason;
        if (string.IsNullOrWhiteSpace(exhaustionReason) && budgetScope.WallClockExhausted)
            exhaustionReason = "Shared wall-clock budget exhausted";
        if (string.IsNullOrWhiteSpace(exhaustionReason) && budgetScope.CallerCancellationRequested)
            exhaustionReason = "Execution cancelled by caller";

        return new ExecutionBudgetEvidence
        {
            StartedAt = budgetScope.StartedAt,
            FinishedAt = DateTime.UtcNow,
            WallClockElapsed = budgetScope.Elapsed,
            WallClockLimitSeconds = budget.MaxDurationSeconds,
            MaximumAttempts = budgetScope.MaximumAttempts,
            AttemptsUsed = attempts.Count,
            InputTokens = agentResult.InputTokens,
            OutputTokens = agentResult.OutputTokens,
            EstimatedCost = agentResult.EstimatedCost,
            Exhausted = !string.IsNullOrWhiteSpace(budgetExhaustionReason) ||
                budgetScope.WallClockExhausted ||
                agentResult.FailureKind == AgentFailureKind.BudgetExceeded,
            ExhaustionReason = exhaustionReason
        };
    }

    private static TaskContract WithRisk(TaskContract contract, RiskLevel risk) => new()
    {
        Id = contract.Id,
        Objective = contract.Objective,
        AcceptanceCriteria = contract.AcceptanceCriteria,
        AcceptanceRequirements = contract.AcceptanceRequirements,
        Scope = contract.Scope,
        Constraints = new TaskConstraints
        {
            SecurityRisk = risk,
            DatabaseMigration = contract.Constraints.DatabaseMigration,
            ExternalDependency = contract.Constraints.ExternalDependency
        },
        Budget = contract.Budget,
        Execution = contract.Execution,
        Verification = contract.Verification,
        Approval = contract.Approval,
        Status = contract.Status,
        CreatedAt = contract.CreatedAt
    };
}
