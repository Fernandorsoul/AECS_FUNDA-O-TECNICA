using AECS.Application.ControlKernel;
using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public sealed class AdaptiveRoutingPolicy
{
    public bool Enabled { get; init; }
    public bool RollbackRequested { get; init; }
    public int MinimumReadyRecords { get; init; } = 50;
    public IReadOnlyCollection<RiskLevel> AllowedRisks { get; init; } =
        [RiskLevel.R0, RiskLevel.R1];
    public string? CanaryRepositoryPath { get; init; }

    public static AdaptiveRoutingPolicy Disabled { get; } = new();
}

public static class AdaptiveRoutingPlanSelector
{
    public static ExecutionPlan Select(
        ExecutionPlan fixedPlan,
        AdaptiveShadowRecommendation? recommendation,
        TaskContract contract,
        string repositoryPath,
        AdaptiveRoutingPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(fixedPlan);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        policy ??= AdaptiveRoutingPolicy.Disabled;
        if (!policy.Enabled ||
            policy.RollbackRequested ||
            recommendation is null ||
            recommendation.DataStatus != AdaptiveShadowDataStatus.Ready ||
            recommendation.Inputs.MatchingRecords < policy.MinimumReadyRecords ||
            !policy.AllowedRisks.Contains(contract.Constraints.SecurityRisk) ||
            !CanaryMatches(repositoryPath, policy.CanaryRepositoryPath))
        {
            return fixedPlan;
        }

        CapabilityPolicyGuard.EnsureNoExpansion(
            fixedPlan.Capabilities,
            recommendation.RecommendedPlan.Capabilities);
        EnsureNoBudgetExpansion(fixedPlan.Budget, recommendation.RecommendedPlan.Budget);

        return new ExecutionPlan
        {
            TaskId = fixedPlan.TaskId,
            Model = string.IsNullOrWhiteSpace(recommendation.RecommendedPlan.Model)
                ? fixedPlan.Model
                : recommendation.RecommendedPlan.Model,
            Budget = fixedPlan.Budget,
            Risk = fixedPlan.Risk,
            Verification = fixedPlan.Verification,
            Capabilities = fixedPlan.Capabilities
        };
    }

    private static bool CanaryMatches(string repositoryPath, string? canaryRepositoryPath)
    {
        if (string.IsNullOrWhiteSpace(canaryRepositoryPath))
            return true;
        return string.Equals(
            Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(canaryRepositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoBudgetExpansion(ExecutionBudget fixedBudget, ExecutionBudget recommended)
    {
        if (recommended.MaxTokens > fixedBudget.MaxTokens ||
            recommended.MaxCostUsd > fixedBudget.MaxCostUsd ||
            recommended.MaxRetries > fixedBudget.MaxRetries ||
            recommended.MaxDurationSeconds > fixedBudget.MaxDurationSeconds ||
            recommended.MaxFilesChanged > fixedBudget.MaxFilesChanged)
        {
            throw new InvalidOperationException(
                "Adaptive routing recommendation attempted to expand the fixed budget.");
        }
    }
}
