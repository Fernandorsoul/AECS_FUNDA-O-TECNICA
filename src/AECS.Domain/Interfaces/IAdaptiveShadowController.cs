using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IAdaptiveShadowController
{
    Task<AdaptiveShadowRecommendation> RecommendAsync(
        string repositoryPath,
        TaskContract task,
        ExecutionPlan fixedPlan,
        CancellationToken cancellationToken);

    Task<AdaptiveShadowReport> CreateReportAsync(
        string repositoryPath,
        int limit,
        CancellationToken cancellationToken);
}
