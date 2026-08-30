using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Domain.Enums;

namespace AECS.Application.Experiments;

public class ExperimentRunner
{
    private readonly StagedExecutionPipeline _pipeline;
    private readonly TaskContractParser _parser = new();

    public ExperimentRunner(StagedExecutionPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public async Task<ExperimentReport> RunAsync(
        string repoPath,
        IEnumerable<string> taskFiles,
        CancellationToken cancellationToken)
    {
        var results = new List<TaskExperimentResult>();

        foreach (var taskFile in taskFiles)
        {
            var contract = _parser.ParseFromFile(taskFile);
            var execution = await _pipeline.RunAsync(repoPath, contract, cancellationToken);
            results.Add(new TaskExperimentResult
            {
                TaskId = execution.Contract.Id,
                Objective = execution.Contract.Objective,
                Risk = execution.Risk,
                Model = execution.Model,
                Decision = execution.Decision.Decision,
                DecisionReason = execution.Decision.Reason,
                Duration = execution.AgentResult.Duration,
                InputTokens = execution.AgentResult.InputTokens,
                OutputTokens = execution.AgentResult.OutputTokens,
                EstimatedCost = execution.AgentResult.EstimatedCost,
                FilesChanged = execution.CandidateChangeSet.ChangedFiles.Count,
                Verifications = execution.VerificationResults.ToDictionary(
                    result => result.Verifier,
                    result => result.Status),
                EvidenceId = execution.EvidenceId,
                OriginalRepositoryUnchanged = execution.OriginalRepositoryUnchanged
            });
        }

        return new ExperimentReport { Results = results };
    }
}
