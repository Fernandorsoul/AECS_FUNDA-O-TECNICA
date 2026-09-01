using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IHistoricalDecisionStore
{
    Task<string> SaveHistoricalDecisionAsync(
        HistoricalDecision decision,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoricalDecision>> LoadHistoricalDecisionsAsync(
        CancellationToken cancellationToken);

    Task<string> SaveHistoricalDecisionSuppressionAsync(
        HistoricalDecisionSuppression suppression,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoricalDecisionSuppression>>
        LoadHistoricalDecisionSuppressionsAsync(CancellationToken cancellationToken);
}
