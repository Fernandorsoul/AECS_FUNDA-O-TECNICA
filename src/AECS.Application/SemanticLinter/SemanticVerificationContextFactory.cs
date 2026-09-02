using AECS.Application.RepositorySnapshots;
using AECS.Application.SymbolGraphs;
using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public static class SemanticVerificationContextFactory
{
    public static VerificationContext CreateError(
        VerificationContext context,
        RepositorySnapshot? baselineSnapshot,
        CSharpSymbolGraph? baselineGraph,
        string error) => new()
        {
            TaskId = context.TaskId,
            AgentRunId = context.AgentRunId,
            RepoPath = context.RepoPath,
            Contract = context.Contract,
            AgentResult = context.AgentResult,
            CandidateChangeSet = context.CandidateChangeSet,
            CommandEvidence = context.CommandEvidence,
            Phase = context.Phase,
            BaselineRepositorySnapshot = baselineSnapshot,
            BaselineCSharpSymbolGraph = baselineGraph,
            SemanticAnalysisError = error
        };

    public static async Task<VerificationContext> CreateAsync(
        VerificationContext context,
        RepositorySnapshot baselineSnapshot,
        CSharpSymbolGraph baselineGraph,
        IReadOnlyCollection<ExecutionCommandEvidence> baselineCommands,
        RepositorySnapshotBuilder snapshotBuilder,
        ICSharpSymbolGraphBuilder symbolGraphBuilder,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidateSnapshot = await snapshotBuilder.BuildCandidateAsync(
                context.RepoPath,
                baselineSnapshot.BaselineCommit,
                context.Contract,
                baselineCommands,
                cancellationToken);
            var candidateGraph = await symbolGraphBuilder.BuildAsync(
                context.RepoPath,
                candidateSnapshot,
                baselineGraph.Limits,
                cancellationToken);
            return Copy(
                context,
                baselineSnapshot,
                candidateSnapshot,
                baselineGraph,
                candidateGraph,
                string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Copy(
                context,
                baselineSnapshot,
                null,
                baselineGraph,
                null,
                $"Semantic snapshot preparation failed: {ex.Message}");
        }
    }

    private static VerificationContext Copy(
        VerificationContext context,
        RepositorySnapshot baselineSnapshot,
        RepositorySnapshot? candidateSnapshot,
        CSharpSymbolGraph baselineGraph,
        CSharpSymbolGraph? candidateGraph,
        string error) => new()
        {
            TaskId = context.TaskId,
            AgentRunId = context.AgentRunId,
            RepoPath = context.RepoPath,
            Contract = context.Contract,
            AgentResult = context.AgentResult,
            CandidateChangeSet = context.CandidateChangeSet,
            CommandEvidence = context.CommandEvidence,
            Phase = context.Phase,
            BaselineRepositorySnapshot = baselineSnapshot,
            CandidateRepositorySnapshot = candidateSnapshot,
            BaselineCSharpSymbolGraph = baselineGraph,
            CandidateCSharpSymbolGraph = candidateGraph,
            SemanticAnalysisError = error
        };
}
