using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IAgentAdapter
{
    Task<AgentRunResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken);
}
