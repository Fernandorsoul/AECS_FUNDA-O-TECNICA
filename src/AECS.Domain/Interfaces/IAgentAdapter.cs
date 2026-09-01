using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IAgentAdapter
{
    AgentContextProfile GetContextProfile(AgentExecutionRequest request)
    {
        var dynamicPromptMaterial = string.Join(
            '\n',
            new[] { request.Objective }
                .Concat(request.AcceptanceCriteria)
                .Concat(request.Scope.Allowed)
                .Concat(request.Scope.Forbidden));
        return AgentContextProfile.Conservative(
            GetType().Name,
            request.Model,
            request.Budget,
            ConservativeTokenCounter.Count(dynamicPromptMaterial) + 512);
    }

    Task<AgentRunResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken);
}
