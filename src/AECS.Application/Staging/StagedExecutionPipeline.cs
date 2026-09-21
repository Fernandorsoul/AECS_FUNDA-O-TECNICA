using AECS.Application.Classification;
using AECS.Application.AdaptiveController;
using AECS.Application.ConstraintLedger;
using AECS.Application.ContextCompiler;
using AECS.Application.Execution;
using AECS.Application.ControlKernel;
using AECS.Application.RepositorySnapshots;
using AECS.Application.SemanticLinter;
using AECS.Application.SymbolGraphs;
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
    public RepositorySnapshot RepositorySnapshot { get; init; } = new();
    public CSharpSymbolGraph CSharpSymbolGraph { get; init; } = new();
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
    public AdaptiveShadowEvidence? AdaptiveShadow { get; init; }
}

public sealed class StagedExecutionPipeline
{
    private static readonly TimeSpan AdaptiveShadowTimeout = TimeSpan.FromSeconds(5);
    private readonly IAgentAdapter _agentAdapter;
    private readonly IStagedProcessRunnerFactory _stagedProcessRunnerFactory;
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly GitWorkspaceManager _workspaceManager;
    private readonly RepositoryContextCompiler _contextCompiler;
    private readonly RiskClassifier _riskClassifier = new();
    private readonly IExecutionController _executionController;
    private readonly DecisionEngine _decisionEngine = new();
    private readonly FileApplicator _fileApplicator = new();
    private readonly AgentExecutionCoordinator _agentExecutionCoordinator;
    private readonly IReadOnlyList<ISecurityScanner> _securityScanners;
    private readonly RepositorySnapshotBuilder _repositorySnapshotBuilder;
    private readonly ICSharpSymbolGraphBuilder _symbolGraphBuilder;
    private readonly IHistoricalDecisionStore? _historicalDecisionStore;
    private readonly IAdaptiveShadowController? _adaptiveShadowController;
    private readonly AdaptiveRoutingPolicy _adaptiveRoutingPolicy;
    private readonly ICompiledContextGate? _compiledContextGate;
    private readonly IConstraintLedgerStore? _constraintLedgerStore;
    private readonly bool _constraintLedgerEnabled;

    public StagedExecutionPipeline(
        IAgentAdapter agentAdapter,
        IProcessRunner processRunner,
        IExecutionEvidenceStore evidenceStore,
        RepositoryContextCompiler? contextCompiler = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        TimeSpan? maximumRetryBackoff = null,
        IStagedProcessRunnerFactory? stagedProcessRunnerFactory = null,
        IReadOnlyList<ISecurityScanner>? securityScanners = null,
        RepositorySnapshotBuilder? repositorySnapshotBuilder = null,
        ICSharpSymbolGraphBuilder? symbolGraphBuilder = null,
        IHistoricalDecisionStore? historicalDecisionStore = null,
        IExecutionController? executionController = null,
        IAdaptiveShadowController? adaptiveShadowController = null,
        AdaptiveRoutingPolicy? adaptiveRoutingPolicy = null,
        ICompiledContextGate? compiledContextGate = null,
        IConstraintLedgerStore? constraintLedgerStore = null,
        bool constraintLedgerEnabled = true)
    {
        _agentAdapter = agentAdapter;
        _stagedProcessRunnerFactory = stagedProcessRunnerFactory ??
            new DefaultStagedProcessRunnerFactory(processRunner);
        _evidenceStore = evidenceStore;
        _workspaceManager = new GitWorkspaceManager(processRunner);
        _contextCompiler = contextCompiler ?? new RepositoryContextCompiler();
        _securityScanners = securityScanners ?? SecurityScanVerifier.CreateDefaultScanners();
        _repositorySnapshotBuilder = repositorySnapshotBuilder ??
            new RepositorySnapshotBuilder(processRunner);
        _symbolGraphBuilder = symbolGraphBuilder ?? new RoslynSymbolGraphBuilder();
        _historicalDecisionStore = historicalDecisionStore ??
            evidenceStore as IHistoricalDecisionStore;
        _executionController = executionController ?? new ExecutionController();
        _adaptiveShadowController = adaptiveShadowController ??
            (evidenceStore is IEvidenceGraphSource graphSource
                ? new AECS.Application.AdaptiveController.AdaptiveController(
                    evidenceStore,
                    graphSource)
                : null);
        _adaptiveRoutingPolicy = adaptiveRoutingPolicy ?? AdaptiveRoutingPolicy.Disabled;
        _compiledContextGate = compiledContextGate;
        _constraintLedgerStore = constraintLedgerStore;
        _constraintLedgerEnabled = constraintLedgerEnabled;
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
        var stateMachine = new TaskStateMachine();
        stateMachine.TransitionTo(TaskState.ContractReady);

        var risk = _riskClassifier.Classify(inputContract);
        var contract = WithRisk(inputContract, risk);
        var plan = await _executionController.PlanAsync(contract, cancellationToken);
        CapabilityPolicyGuard.EnsureNoExpansion(
            contract.Execution.EffectiveCapabilities,
            plan.Capabilities);
        stateMachine.TransitionTo(TaskState.Planned);

        var baseline = await _workspaceManager.CaptureBaselineAsync(
            repositoryPath,
            cancellationToken);
        _evidenceStore.EnsureRepositoryIsolation(baseline.RepositoryPath);
        var shadowRecommendation = await RecommendInShadowAsync(
            baseline.RepositoryPath,
            contract,
            plan,
            cancellationToken);
        var effectivePlan = AdaptiveRoutingPlanSelector.Select(
            plan,
            shadowRecommendation,
            contract,
            baseline.RepositoryPath,
            _adaptiveRoutingPolicy);
        using var budgetScope = new ExecutionBudgetScope(
            effectivePlan.Budget,
            cancellationToken);

        var agentRunId = Guid.NewGuid().ToString("N");
        var startedAt = DateTime.UtcNow;
        stateMachine.TransitionTo(TaskState.BaselineVerifying);

        var baselineCommands = new List<ExecutionCommandEvidence>();
        List<VerificationResult> baselineVerificationResults;
        RepositorySnapshot repositorySnapshot;
        CSharpSymbolGraph symbolGraph;
        await using (var preflightWorkspace = await _workspaceManager.CreateWorkspaceAsync(
            baseline,
            budgetScope.Token))
        {
            var stagedProcessRunner = await _stagedProcessRunnerFactory.CreateAsync(
                preflightWorkspace.Path,
                contract.Execution,
                budgetScope.Token);
            var baselineContext = new VerificationContext
            {
                TaskId = contract.Id,
                AgentRunId = agentRunId,
                RepoPath = preflightWorkspace.Path,
                Contract = contract,
                CommandEvidence = baselineCommands,
                Phase = "baseline"
            };
            await ToolVersionProbe.CaptureAsync(
                stagedProcessRunner,
                baselineContext,
                budgetScope.Token,
                () => budgetScope.RemainingDuration);
            repositorySnapshot = await _repositorySnapshotBuilder.BuildAsync(
                preflightWorkspace.Path,
                baseline.Commit,
                contract,
                baselineCommands,
                budgetScope.Token);
            symbolGraph = await _symbolGraphBuilder.BuildAsync(
                preflightWorkspace.Path,
                repositorySnapshot,
                cancellationToken: budgetScope.Token);
            baselineVerificationResults = await VerifyBaselineAsync(
                stagedProcessRunner,
                baselineContext,
                budgetScope.Token,
                () => budgetScope.RemainingDuration);
        }

        await _workspaceManager.EnsureBaselineUnchangedAsync(
            baseline,
            CancellationToken.None);

        ConstraintLedgerService? constraintLedger = null;
        ConstraintSetRef? constraintSet = null;
        if (_constraintLedgerEnabled)
        {
            constraintLedger = new ConstraintLedgerService();
            ConstraintLedgerProjector.Build(
                contract,
                repositorySnapshot.SnapshotHash,
                constraintLedger);
            if (_constraintLedgerStore is not null)
            {
                var persisted = await _constraintLedgerStore.LoadAsync(
                    baseline.RepositoryPath,
                    cancellationToken);
                if (persisted is not null)
                {
                    constraintLedger.AppendHistory(persisted.Records.Where(record =>
                        record.Status is not (ConstraintStatus.Active or
                            ConstraintStatus.PendingVerification)));
                    foreach (var record in persisted.Records.Where(record =>
                        record.Status is ConstraintStatus.Active or
                            ConstraintStatus.PendingVerification))
                    {
                        constraintLedger.Ingest(record);
                    }

                    constraintLedger.AppendConflicts(persisted.Conflicts);
                }
            }

            constraintSet = constraintLedger.CreateSetRef();
        }

        var baselineFailures = RequiredBaselineFailures(
            contract,
            baselineVerificationResults);
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
                effectivePlan.Model,
                shadowRecommendation,
                baseline,
                repositorySnapshot,
                symbolGraph,
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
                budgetScope,
                constraintLedger,
                constraintSet);
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
            var stagedProcessRunner = await _stagedProcessRunnerFactory.CreateAsync(
                workspace.Path,
                contract.Execution,
                budgetScope.Token);
            var baseAgentRequest = new AgentExecutionRequest
            {
                TaskId = contract.Id,
                Objective = contract.Objective,
                AcceptanceCriteria = contract.AcceptanceCriteria,
                RepoPath = workspace.Path,
                Scope = contract.Scope,
                Budget = contract.Budget,
                Risk = risk,
                Model = effectivePlan.Model
            };
            var contextProfile = _agentAdapter.GetContextProfile(baseAgentRequest);
            var compiledContext = _contextCompiler.Compile(
                workspace.Path,
                contract,
                baseline.Commit,
                symbolGraph: symbolGraph,
                agentProfile: contextProfile,
                constraints: constraintLedger?.GetActive());
            contextManifest = compiledContext.Manifest;
            _compiledContextGate?.EnsureAccepted(contract, compiledContext);

            var agentOutcome = await _agentExecutionCoordinator.ExecuteAsync(
                new AgentExecutionRequest
                {
                    TaskId = baseAgentRequest.TaskId,
                    Objective = baseAgentRequest.Objective,
                    AcceptanceCriteria = baseAgentRequest.AcceptanceCriteria,
                    RepoPath = baseAgentRequest.RepoPath,
                    Scope = baseAgentRequest.Scope,
                    Budget = baseAgentRequest.Budget,
                    Risk = baseAgentRequest.Risk,
                    Model = baseAgentRequest.Model,
                    CodeContext = compiledContext.CodeContext,
                    ContextPrompt = compiledContext.Prompt
                }, budgetScope);
            agentResult = agentOutcome.Result;
            agentAttempts = agentOutcome.Attempts;
            budgetExhaustionReason = agentOutcome.BudgetExhaustionReason;

            var applicationResult = agentResult.Success
                ? _fileApplicator.ApplyChanges(
                    agentResult.StdOut,
                    workspace.Path,
                    contract.Execution.WorkingDirectory)
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
                CommandEvidence = candidateCommands,
                Phase = "candidate",
                Trajectory = TrajectoryEvidence.Capture(contract)
            };

            verificationResults = await VerifyAsync(
                stagedProcessRunner,
                verificationContext,
                applicationResult,
                repositorySnapshot,
                symbolGraph,
                baselineCommands,
                BaselineSecurityFingerprints(baselineVerificationResults),
                acceptanceCriteriaResults,
                agentResult.FailureKind is AgentFailureKind.Cancelled or AgentFailureKind.BudgetExceeded
                    ? CancellationToken.None
                    : budgetScope.Token,
                () => budgetScope.RemainingDuration,
                constraintLedger,
                constraintSet);
            decision = _decisionEngine.Decide(
                verificationResults,
                contract,
                requireConstraintLedger: _constraintLedgerEnabled);
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
            effectivePlan.Model,
            shadowRecommendation,
            baseline,
            repositorySnapshot,
            symbolGraph,
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
            budgetScope,
            constraintLedger,
            constraintSet);
    }

    private async Task<StagedExecutionResult> PublishAsync(
        TaskContract contract,
        RiskLevel risk,
        string model,
        AdaptiveShadowRecommendation? shadowRecommendation,
        BaselineSnapshot baseline,
        RepositorySnapshot repositorySnapshot,
        CSharpSymbolGraph symbolGraph,
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
        ExecutionBudgetScope budgetScope,
        ConstraintLedgerService? constraintLedger,
        ConstraintSetRef? constraintSet)
    {
        await _workspaceManager.EnsureBaselineUnchangedAsync(
            baseline,
            CancellationToken.None);
        if (constraintLedger is not null && constraintSet is not null)
        {
            var refreshedSet = constraintLedger.CreateSetRef();
            if (!string.Equals(
                    refreshedSet.CanonicalSha256,
                    constraintSet.CanonicalSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Constraint set changed during execution; derived plans and context " +
                    $"must be revalidated (captured {constraintSet.CanonicalSha256}, " +
                    $"now {refreshedSet.CanonicalSha256}).");
            }
        }

        ValidateCommandEnvironments(
            contract.Execution,
            baselineCommands.Concat(candidateCommands));

        if (_constraintLedgerStore is not null && constraintLedger is not null)
        {
            await _constraintLedgerStore.SaveAsync(
                baseline.RepositoryPath,
                new ConstraintLedgerSnapshot
                {
                    Records = constraintLedger.AllRecords.ToList(),
                    Conflicts = constraintLedger.AllConflicts.ToList()
                },
                CancellationToken.None);
        }

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

        var baselineFailed = RequiredBaselineFailures(
            contract,
            baselineVerificationResults).Count > 0;
        var budgetEvidence = CreateBudgetEvidence(
            contract.Budget,
            budgetScope,
            agentResult,
            agentAttempts,
            budgetExhaustionReason);
        var adaptiveShadow = shadowRecommendation is null
            ? null
            : EvaluateShadow(
                shadowRecommendation,
                contract,
                model,
                contextManifest,
                agentResult,
                decision);
        var evidence = new ExecutionEvidence
        {
            TaskContract = contract,
            AgentRun = agentRun,
            AgentResult = agentResult,
            AgentAttempts = agentAttempts,
            BudgetUsage = budgetEvidence,
            Baseline = baseline,
            RepositorySnapshot = repositorySnapshot,
            CSharpSymbolGraph = symbolGraph,
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
                        : DecisionEngine.GetRequiredVerifiers(
                            contract,
                            includeConstraintLedger: constraintLedger is not null))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DecidedAt = DateTime.UtcNow
            },
            AdaptiveShadow = adaptiveShadow,
            ConstraintSet = constraintSet,
            ConstraintRecords = constraintLedger?.GetActive().ToList(),
            Trajectory = TrajectoryEvidence.Capture(contract),
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
            AdaptiveShadow = adaptiveShadow,
            Baseline = baseline,
            RepositorySnapshot = repositorySnapshot,
            CSharpSymbolGraph = symbolGraph,
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

    private async Task<AdaptiveShadowRecommendation?> RecommendInShadowAsync(
        string repositoryPath,
        TaskContract contract,
        ExecutionPlan fixedPlan,
        CancellationToken cancellationToken)
    {
        if (_adaptiveShadowController is null)
            return null;
        using var shadowTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        shadowTimeout.CancelAfter(AdaptiveShadowTimeout);
        try
        {
            var recommendation = await _adaptiveShadowController.RecommendAsync(
                repositoryPath,
                contract,
                fixedPlan,
                shadowTimeout.Token);
            EnsureShadowRecommendationSafe(fixedPlan, recommendation);
            return recommendation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AdaptiveShadowRecommendation
            {
                DataStatus = AdaptiveShadowDataStatus.InvalidHistory,
                Inputs = new AdaptiveShadowInputs
                {
                    Risk = contract.Constraints.SecurityRisk,
                    TaskType = AdaptiveTaskTypeClassifier.Classify(contract.Objective),
                    ObjectiveFingerprint = AdaptiveTaskTypeClassifier.Fingerprint(
                        contract.Objective),
                    InvalidOrTamperedRecords = 1
                },
                FixedPlan = Snapshot(fixedPlan),
                RecommendedPlan = Snapshot(fixedPlan),
                Justification =
                    "Shadow analysis failed closed; the deterministic fixed plan is recommended.",
                Diagnostics = [$"Adaptive shadow unavailable: {ex.GetType().Name}: {ex.Message}"]
            };
        }
    }

    private static AdaptiveShadowEvidence EvaluateShadow(
        AdaptiveShadowRecommendation recommendation,
        TaskContract contract,
        string executedModel,
        ContextManifest context,
        AgentRunResult result,
        DecisionResult decision)
    {
        var fixedPlan = recommendation.FixedPlan;
        var capabilitiesPreserved = string.Equals(
            ExecutionCapabilityPolicyFingerprint.Create(fixedPlan.Capabilities),
            ExecutionCapabilityPolicyFingerprint.Create(
                contract.Execution.EffectiveCapabilities),
            StringComparison.Ordinal);
        return new AdaptiveShadowEvidence
        {
            Recommendation = recommendation,
            Evaluation = new AdaptiveShadowEvaluation
            {
                FixedControllerDecision = decision.Decision,
                FixedControllerState = decision.TargetState,
                FixedControllerSucceeded = decision.Decision == TaskDecision.Verified,
                ExecutedModel = executedModel,
                ExecutedContextStrategy = context.Strategy,
                ExecutedBudget = contract.Budget,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                AccountedCostUsd = result.UsageAccounting?.AccountedCostUsd,
                DurationSeconds = result.Duration.TotalSeconds,
                RecommendationAgreedWithFixedModel = string.Equals(
                    recommendation.RecommendedPlan.Model,
                    executedModel,
                    StringComparison.Ordinal),
                RecommendationAgreedWithFixedContext = string.Equals(
                    recommendation.RecommendedPlan.ContextStrategy,
                    context.Strategy,
                    StringComparison.Ordinal),
                FixedPlanPreserved = string.Equals(
                        fixedPlan.Model,
                        executedModel,
                        StringComparison.Ordinal) &&
                    BudgetEquals(fixedPlan.Budget, contract.Budget) &&
                    capabilitiesPreserved,
                CounterfactualExecuted = false
            }
        };
    }

    private static void EnsureShadowRecommendationSafe(
        ExecutionPlan fixedPlan,
        AdaptiveShadowRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(recommendation.RecommendedPlan);
        CapabilityPolicyGuard.EnsureNoExpansion(
            fixedPlan.Capabilities,
            recommendation.RecommendedPlan.Capabilities);
        var budget = recommendation.RecommendedPlan.Budget;
        if (budget.MaxTokens > fixedPlan.Budget.MaxTokens ||
            budget.MaxCostUsd > fixedPlan.Budget.MaxCostUsd ||
            budget.MaxRetries > fixedPlan.Budget.MaxRetries ||
            budget.MaxDurationSeconds > fixedPlan.Budget.MaxDurationSeconds ||
            budget.MaxFilesChanged > fixedPlan.Budget.MaxFilesChanged)
        {
            throw new InvalidOperationException(
                "Adaptive shadow recommendation attempted to expand the fixed budget.");
        }
    }

    private static AdaptiveShadowPlan Snapshot(ExecutionPlan plan) => new()
    {
        Model = plan.Model,
        ContextStrategy = "governed-by-context-compiler",
        Budget = plan.Budget,
        Verification = plan.Verification,
        Capabilities = plan.Capabilities
    };

    private static bool BudgetEquals(ExecutionBudget left, ExecutionBudget right) =>
        left.MaxTokens == right.MaxTokens &&
        left.MaxCostUsd == right.MaxCostUsd &&
        left.MaxRetries == right.MaxRetries &&
        left.MaxDurationSeconds == right.MaxDurationSeconds &&
        left.MaxFilesChanged == right.MaxFilesChanged;

    private async Task<List<VerificationResult>> VerifyBaselineAsync(
        IProcessRunner stagedProcessRunner,
        VerificationContext context,
        CancellationToken cancellationToken,
        Func<TimeSpan> remainingDuration)
    {
        var results = new List<VerificationResult>();

        if (context.Contract.Verification.Build)
        {
            results.Add(await RunVerifierAsync(
                new BuildVerifier(stagedProcessRunner, remainingDuration),
                context,
                cancellationToken));
        }

        var buildPassed = results
            .Where(result => result.Verifier == "Build")
            .All(result => result.Status == VerificationStatus.Pass);
        if (context.Contract.Execution.TestSuites is null &&
            (context.Contract.Verification.UnitTests ||
             context.Contract.Verification.IntegrationTests))
        {
            results.Add(buildPassed
                ? await RunVerifierAsync(
                    new TestVerifier(stagedProcessRunner, remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Tests", "Baseline build prerequisite failed"));
        }
        else if (context.Contract.Execution.TestSuites is not null)
        {
            foreach (var suite in context.Contract.Execution.TestSuites.EnabledSuites)
            {
                results.Add(buildPassed
                    ? await RunVerifierAsync(
                        new TestSuiteVerifier(
                            stagedProcessRunner,
                            suite.Category,
                            suite.Profile,
                            remainingDuration),
                        context,
                        cancellationToken)
                    : Skipped(
                        context.AgentRunId,
                        TestSuiteVerifier.NameFor(suite.Category),
                        "Baseline build prerequisite failed"));
            }
        }

        if (context.Contract.Verification.SecurityScan)
        {
            results.Add(buildPassed
                ? await RunVerifierAsync(
                    new SecurityScanVerifier(
                        stagedProcessRunner,
                        _securityScanners,
                        isBaseline: true,
                        remainingDuration: remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(
                    context.AgentRunId,
                    SecurityScanVerifier.VerifierName,
                    "Baseline build prerequisite failed"));
        }

        return results;
    }

    private static IReadOnlySet<string> GetRequiredBaselineVerifiers(TaskContract contract)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (contract.Verification.Build)
            required.Add("Build");
        // Tests are not required in baseline — failing baseline tests are expected
        // for bug-fix tasks. Tests are verified on the candidate after agent changes.
        if (contract.Verification.SecurityScan)
            required.Add(SecurityScanVerifier.VerifierName);
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
        IProcessRunner stagedProcessRunner,
        VerificationContext context,
        FileApplicatorResult applicationResult,
        RepositorySnapshot baselineSnapshot,
        CSharpSymbolGraph baselineGraph,
        IReadOnlyCollection<ExecutionCommandEvidence> baselineCommands,
        IReadOnlySet<string> baselineSecurityFingerprints,
        List<AcceptanceCriterionResult> acceptanceCriteriaResults,
        CancellationToken cancellationToken,
        Func<TimeSpan> remainingDuration,
        ConstraintLedgerService? constraintLedger,
        ConstraintSetRef? constraintSet)
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
                    new BuildVerifier(stagedProcessRunner, remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Build", "Trust-boundary prerequisite failed"));
        }

        var buildPassed = results
            .Where(result => result.Verifier == "Build")
            .All(result => result.Status == VerificationStatus.Pass);

        if (context.Contract.Execution.TestSuites is null &&
            (context.Contract.Verification.UnitTests || context.Contract.Verification.IntegrationTests))
        {
            results.Add(prerequisitesPassed && buildPassed
                ? await RunVerifierAsync(
                    new TestVerifier(stagedProcessRunner, remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Tests", "Trust-boundary or build prerequisite failed"));
        }
        else if (context.Contract.Execution.TestSuites is not null)
        {
            foreach (var suite in context.Contract.Execution.TestSuites.EnabledSuites)
            {
                results.Add(prerequisitesPassed && buildPassed
                    ? await RunVerifierAsync(
                        new TestSuiteVerifier(
                            stagedProcessRunner,
                            suite.Category,
                            suite.Profile,
                            remainingDuration),
                        context,
                        cancellationToken)
                    : Skipped(
                        context.AgentRunId,
                        TestSuiteVerifier.NameFor(suite.Category),
                        "Trust-boundary or build prerequisite failed"));
            }
        }

        if (context.Contract.Verification.SecurityScan)
        {
            results.Add(prerequisitesPassed && buildPassed
                ? await RunVerifierAsync(
                    new SecurityScanVerifier(
                        stagedProcessRunner,
                        _securityScanners,
                        baselineSecurityFingerprints,
                        remainingDuration: remainingDuration),
                    context,
                    cancellationToken)
                : Skipped(
                    context.AgentRunId,
                    SecurityScanVerifier.VerifierName,
                    "Trust-boundary or build prerequisite failed"));
        }

        if (prerequisitesPassed && buildPassed)
        {
            var semanticContext = await SemanticVerificationContextFactory.CreateAsync(
                context,
                baselineSnapshot,
                baselineGraph,
                baselineCommands,
                _repositorySnapshotBuilder,
                _symbolGraphBuilder,
                cancellationToken);
            var semanticVerifiers = new IVerifier[]
            {
                new EB001Verifier(),
                new EB002Verifier(),
                new EB003Verifier(),
                new EB004Verifier()
            };

            foreach (var verifier in semanticVerifiers)
                results.Add(await RunVerifierAsync(verifier, semanticContext, cancellationToken));

            var historicalSelection = await HistoricalDecisionSelector.LoadAsync(
                semanticContext,
                _historicalDecisionStore,
                DateTime.UtcNow,
                cancellationToken);
            results.Add(await RunVerifierAsync(
                new EB005Verifier(historicalSelection),
                semanticContext,
                cancellationToken));
        }

        if (AcceptanceCriteriaVerifier.GetEffectiveCriteria(context.Contract).Count > 0)
        {
            var acceptance = await new AcceptanceCriteriaVerifier(
                stagedProcessRunner,
                remainingDuration).VerifyAsync(
                context,
                results,
                prerequisitesPassed && buildPassed,
                cancellationToken);
            acceptanceCriteriaResults.AddRange(acceptance.Criteria);
            results.Add(acceptance.AggregateResult);
        }

        if (constraintLedger is not null && constraintSet is not null)
        {
            results.Add(await new ConstraintLedgerVerifier(constraintLedger, constraintSet)
                .VerifyAsync(context, results, cancellationToken));
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

    private static IReadOnlySet<string> BaselineSecurityFingerprints(
        IEnumerable<VerificationResult> results) => results
        .Where(result => result.Verifier == SecurityScanVerifier.VerifierName)
        .SelectMany(result => result.SecurityScan?.Findings ?? [])
        .Where(finding => finding.Disposition != SecurityFindingDisposition.Suppressed)
        .Select(finding => finding.Fingerprint)
        .ToHashSet(StringComparer.Ordinal);

    private static List<string> RequiredBaselineFailures(
        TaskContract contract,
        IReadOnlyList<VerificationResult> results)
    {
        var failures = new List<string>();
        foreach (var verifier in GetRequiredBaselineVerifiers(contract))
        {
            var matches = results.Where(result => result.Verifier.Equals(
                    verifier,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                failures.Add($"{verifier}: required baseline verifier result is missing");
            else if (matches.Count > 1)
                failures.Add($"{verifier}: duplicate baseline verifier results are ambiguous");
            else if (matches[0].Status != VerificationStatus.Pass)
                failures.Add($"{matches[0].Verifier}: {matches[0].Status} - {matches[0].Message}");
        }
        return failures;
    }

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

    private static void ValidateCommandEnvironments(
        RepositoryExecutionProfile profile,
        IEnumerable<ExecutionCommandEvidence> commands)
    {
        foreach (var command in commands)
        {
            var environment = command.Environment ?? throw new InvalidOperationException(
                "Staged command evidence is missing its execution environment.");
            var capabilities = environment.Capabilities ?? throw new InvalidOperationException(
                "Staged command evidence is missing its capability decision.");
            var expectedCapabilities = profile.EffectiveCapabilities;
            if (capabilities.PolicyVersion != expectedCapabilities.Version ||
                capabilities.Authority != expectedCapabilities.Authority ||
                capabilities.PolicyHash !=
                    ExecutionCapabilityPolicyFingerprint.Create(expectedCapabilities))
            {
                throw new InvalidOperationException(
                    "Staged command evidence does not match the authoritative capability policy.");
            }
            if (profile.EffectiveRuntime == RepositoryExecutionProfile.DockerRuntime)
            {
                var sandbox = profile.Sandbox ?? new SandboxExecutionProfile();
                var separator = sandbox.Image.LastIndexOf('@');
                if (environment.Runtime != RepositoryExecutionProfile.DockerRuntime ||
                    separator <= 0 ||
                    environment.Image != sandbox.Image[..separator] ||
                    !string.Equals(
                        environment.ImageDigest,
                        sandbox.Image[(separator + 1)..],
                        StringComparison.OrdinalIgnoreCase) ||
                    environment.CpuLimit != sandbox.CpuLimit ||
                    environment.MemoryLimit != sandbox.MemoryLimit ||
                    environment.ProcessLimit != sandbox.ProcessLimit ||
                    environment.NetworkMode != (sandbox.NetworkAccess ? "bridge" : "none") ||
                    environment.DevelopmentHostOverride)
                {
                    throw new InvalidOperationException(
                        "Staged command evidence does not match the required Docker sandbox policy.");
                }
            }
            else if (environment.Runtime != RepositoryExecutionProfile.HostRuntime ||
                     !environment.DevelopmentHostOverride)
            {
                throw new InvalidOperationException(
                    "Host execution evidence must be marked as an explicit development override.");
            }
        }
    }

    private static TaskContract WithRisk(TaskContract contract, RiskLevel risk) =>
        TaskContractIntegrity.Seal(new TaskContract
        {
            SchemaVersion = TaskContractSchema.CurrentVersion,
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
        });
}
