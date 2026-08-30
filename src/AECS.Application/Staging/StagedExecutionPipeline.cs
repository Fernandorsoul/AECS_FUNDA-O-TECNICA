using AECS.Application.Classification;
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
    public BaselineSnapshot Baseline { get; init; } = new();
    public CandidateChangeSet CandidateChangeSet { get; init; } = new();
    public IReadOnlyList<VerificationResult> VerificationResults { get; init; } = [];
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
    private readonly RiskClassifier _riskClassifier = new();
    private readonly ExecutionController _executionController = new();
    private readonly DecisionEngine _decisionEngine = new();
    private readonly FileApplicator _fileApplicator = new();

    public StagedExecutionPipeline(
        IAgentAdapter agentAdapter,
        IProcessRunner processRunner,
        IExecutionEvidenceStore evidenceStore)
    {
        _agentAdapter = agentAdapter;
        _processRunner = processRunner;
        _evidenceStore = evidenceStore;
        _workspaceManager = new GitWorkspaceManager(processRunner);
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
        stateMachine.TransitionTo(TaskState.Planned);

        // The clean baseline is captured before an agent receives a repository path.
        var baseline = await _workspaceManager.CaptureBaselineAsync(
            repositoryPath,
            cancellationToken);
        _evidenceStore.EnsureRepositoryIsolation(baseline.RepositoryPath);
        var workspace = await _workspaceManager.CreateWorkspaceAsync(
            baseline,
            cancellationToken);

        var agentRunId = Guid.NewGuid().ToString("N");
        var startedAt = DateTime.UtcNow;
        AgentRunResult agentResult;
        FileApplicatorResult applicationResult;
        CandidateChangeSet candidate;
        List<VerificationResult> verificationResults;
        DecisionResult decision;

        try
        {
            stateMachine.TransitionTo(TaskState.Running);
            agentResult = await _agentAdapter.ExecuteAsync(new AgentExecutionRequest
            {
                TaskId = contract.Id,
                Objective = contract.Objective,
                AcceptanceCriteria = contract.AcceptanceCriteria,
                RepoPath = workspace.Path,
                Scope = contract.Scope,
                Budget = contract.Budget,
                Risk = risk,
                Model = plan.Model
            }, cancellationToken);

            applicationResult = agentResult.Success
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
                cancellationToken);
            stateMachine.TransitionTo(TaskState.CandidateProduced);
            stateMachine.TransitionTo(TaskState.Verifying);

            var verificationContext = new VerificationContext
            {
                TaskId = contract.Id,
                AgentRunId = agentRunId,
                RepoPath = workspace.Path,
                Contract = contract,
                AgentResult = agentResult,
                CandidateChangeSet = candidate
            };

            verificationResults = await VerifyAsync(
                verificationContext,
                applicationResult,
                cancellationToken);
            decision = _decisionEngine.Decide(verificationResults, contract);
            stateMachine.TransitionTo(decision.TargetState);
        }
        finally
        {
            await workspace.DisposeAsync();
        }

        // A decision is not publishable until the source checkout is proven intact.
        await _workspaceManager.EnsureBaselineUnchangedAsync(
            baseline,
            CancellationToken.None);

        var agentRun = new AgentRun
        {
            Id = Guid.Parse(agentRunId),
            TaskId = contract.Id,
            AgentType = _agentAdapter.GetType().Name,
            Provider = _agentAdapter.GetType().Namespace ?? string.Empty,
            Model = plan.Model,
            StartedAt = startedAt,
            FinishedAt = startedAt + agentResult.Duration,
            InputTokens = agentResult.InputTokens,
            OutputTokens = agentResult.OutputTokens,
            EstimatedCost = agentResult.EstimatedCost,
            ExitReason = agentResult.ExitReason,
            // Telemetry only. Policy and decisions use CandidateChangeSet.
            FilesChanged = agentResult.FilesChanged.ToList()
        };

        var evidence = new ExecutionEvidence
        {
            TaskContract = contract,
            AgentRun = agentRun,
            AgentResult = agentResult,
            Baseline = baseline,
            CandidateChangeSet = candidate,
            VerificationResults = verificationResults,
            FinalDecision = new FinalDecisionRecord
            {
                Decision = decision.Decision,
                State = decision.TargetState,
                Reason = decision.Reason,
                RequiredVerifiers = DecisionEngine.GetRequiredVerifiers(contract)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DecidedAt = DateTime.UtcNow
            },
            StateTransitions = stateMachine.History
                .Select(item => $"{item.From}->{item.To}@{item.At:O}")
                .ToList()
        };

        var evidenceLocation = await _evidenceStore.SaveAsync(evidence, cancellationToken);
        await _workspaceManager.EnsureBaselineUnchangedAsync(
            baseline,
            CancellationToken.None);

        return new StagedExecutionResult
        {
            Contract = contract,
            Risk = risk,
            Model = plan.Model,
            AgentRun = agentRun,
            AgentResult = agentResult,
            Baseline = baseline,
            CandidateChangeSet = candidate,
            VerificationResults = verificationResults,
            Decision = decision,
            FinalState = stateMachine.CurrentState,
            EvidenceId = evidence.Id,
            EvidenceLocation = evidenceLocation,
            OriginalRepositoryUnchanged = true
        };
    }

    private async Task<List<VerificationResult>> VerifyAsync(
        VerificationContext context,
        FileApplicatorResult applicationResult,
        CancellationToken cancellationToken)
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
                ? await RunVerifierAsync(new BuildVerifier(_processRunner), context, cancellationToken)
                : Skipped(context.AgentRunId, "Build", "Trust-boundary prerequisite failed"));
        }

        var buildPassed = results
            .Where(result => result.Verifier == "Build")
            .All(result => result.Status == VerificationStatus.Pass);

        if (context.Contract.Verification.UnitTests || context.Contract.Verification.IntegrationTests)
        {
            results.Add(prerequisitesPassed && buildPassed
                ? await RunVerifierAsync(new TestVerifier(_processRunner), context, cancellationToken)
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

    private static TaskContract WithRisk(TaskContract contract, RiskLevel risk) => new()
    {
        Id = contract.Id,
        Objective = contract.Objective,
        AcceptanceCriteria = contract.AcceptanceCriteria,
        Scope = contract.Scope,
        Constraints = new TaskConstraints
        {
            SecurityRisk = risk,
            DatabaseMigration = contract.Constraints.DatabaseMigration,
            ExternalDependency = contract.Constraints.ExternalDependency
        },
        Budget = contract.Budget,
        Verification = contract.Verification,
        Approval = contract.Approval,
        Status = contract.Status,
        CreatedAt = contract.CreatedAt
    };
}
