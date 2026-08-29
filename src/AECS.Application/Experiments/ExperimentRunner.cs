using AECS.Application.Classification;
using AECS.Application.ContextCompiler;
using AECS.Application.ControlKernel;
using AECS.Application.Parsing;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Experiments;

public class ExperimentRunner
{
    private readonly IAgentAdapter _agentAdapter;
    private readonly TaskContractParser _parser = new();
    private readonly RiskClassifier _riskClassifier = new();
    private readonly ExecutionController _executionController = new();
    private readonly ControlKernel.ControlKernel _kernel = new();
    private readonly DecisionEngine _decisionEngine = new();
    private readonly CodebaseIndexer _indexer = new();
    private readonly ContextSelector _contextSelector = new();

    public ExperimentRunner(IAgentAdapter agentAdapter)
    {
        _agentAdapter = agentAdapter;
    }

    public async Task<ExperimentReport> RunAsync(
        string repoPath,
        IEnumerable<string> taskFiles,
        CancellationToken cancellationToken)
    {
        var results = new List<TaskExperimentResult>();

        foreach (var taskFile in taskFiles)
        {
            var result = await RunSingleAsync(repoPath, taskFile, cancellationToken);
            results.Add(result);
        }

        return new ExperimentReport
        {
            Results = results
        };
    }

    private async Task<TaskExperimentResult> RunSingleAsync(
        string repoPath,
        string taskFile,
        CancellationToken cancellationToken)
    {
        // 1. Load and classify
        var contract = _parser.ParseFromFile(taskFile);
        var risk = _riskClassifier.Classify(contract);

        contract = new TaskContract
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

        // 2. Plan execution
        var plan = await _executionController.PlanAsync(contract, cancellationToken);

        // 3. Context Compiler — select relevant files
        var codeContext = new Dictionary<string, string>();
        try
        {
            var srcPath = Path.Combine(repoPath, "Backend");
            if (Directory.Exists(srcPath))
            {
                var index = _indexer.Index(srcPath);
                var contextPackage = _contextSelector.Select(
                    index, contract.Id, contract.Objective, contract.Scope.Allowed);

                // Read file contents for context (limit to avoid token overflow)
                var totalChars = 0;
                var maxChars = 20000; // ~5000 tokens
                foreach (var file in contextPackage.SelectedFiles)
                {
                    var fullPath = Path.Combine(repoPath, file);
                    if (File.Exists(fullPath) && !fullPath.Contains("obj") && !fullPath.Contains("bin"))
                    {
                        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
                        if (totalChars + content.Length > maxChars)
                            break;
                        codeContext[file] = content;
                        totalChars += content.Length;
                    }
                }
            }
        }
        catch
        {
            // Context compilation is best-effort
        }

        // 4. Execute agent
        var request = new AgentExecutionRequest
        {
            TaskId = contract.Id,
            Objective = contract.Objective,
            AcceptanceCriteria = contract.AcceptanceCriteria,
            RepoPath = repoPath,
            Scope = contract.Scope,
            Budget = contract.Budget,
            Risk = risk,
            Model = plan.Model,
            CodeContext = codeContext
        };

        var agentResult = await _agentAdapter.ExecuteAsync(request, cancellationToken);

        // 4. Control Kernel
        var kernelDecision = _kernel.ValidateExecution(contract, agentResult);

        if (!kernelDecision.Allowed)
        {
            return new TaskExperimentResult
            {
                TaskId = contract.Id,
                Objective = contract.Objective,
                Risk = risk,
                Model = plan.Model,
                Decision = TaskDecision.Rejected,
                DecisionReason = kernelDecision.Reason,
                Duration = agentResult.Duration,
                InputTokens = agentResult.InputTokens,
                OutputTokens = agentResult.OutputTokens,
                EstimatedCost = agentResult.EstimatedCost,
                FilesChanged = agentResult.FilesChanged.Count,
                Verifications = new Dictionary<string, VerificationStatus>
                {
                    [kernelDecision.TargetState.ToString()] = VerificationStatus.Fail
                }
            };
        }

        // 5. Run verifiers
        var verificationContext = new VerificationContext
        {
            TaskId = contract.Id,
            AgentRunId = Guid.NewGuid().ToString(),
            RepoPath = repoPath,
            Contract = contract,
            AgentResult = agentResult
        };

        var verifiers = new List<IVerifier>
        {
            new BuildVerifier(),
            new TestVerifier(),
            new ScopeVerifier(),
            new BudgetVerifier()
        };

        var verificationResults = new List<VerificationResult>();
        var verificationStatuses = new Dictionary<string, VerificationStatus>();

        foreach (var verifier in verifiers)
        {
            var vResult = await verifier.VerifyAsync(verificationContext, cancellationToken);
            verificationResults.Add(vResult);
            verificationStatuses[verifier.Name] = vResult.Status;
        }

        // 6. Decision
        var decision = _decisionEngine.Decide(verificationResults, contract);

        return new TaskExperimentResult
        {
            TaskId = contract.Id,
            Objective = contract.Objective,
            Risk = risk,
            Model = plan.Model,
            Decision = decision.Decision,
            DecisionReason = decision.Reason,
            Duration = agentResult.Duration,
            InputTokens = agentResult.InputTokens,
            OutputTokens = agentResult.OutputTokens,
            EstimatedCost = agentResult.EstimatedCost,
            FilesChanged = agentResult.FilesChanged.Count,
            Verifications = verificationStatuses
        };
    }
}
