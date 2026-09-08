using AECS.Application.AdaptiveController;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class AdaptiveRoutingPlanSelectorTests
{
    [Fact]
    public void DisabledPolicy_PreservesFixedPlan()
    {
        var fixedPlan = Plan("fixed-model");
        var recommendation = Recommendation("adaptive-model", matchingRecords: 100);

        var selected = AdaptiveRoutingPlanSelector.Select(
            fixedPlan,
            recommendation,
            Contract(RiskLevel.R1),
            Environment.CurrentDirectory,
            AdaptiveRoutingPolicy.Disabled);

        selected.Should().BeSameAs(fixedPlan);
    }

    [Fact]
    public void EnabledPolicy_UsesReadyBoundedModelRecommendationOnly()
    {
        var fixedPlan = Plan("fixed-model");
        var recommendation = Recommendation("adaptive-model", matchingRecords: 100);

        var selected = AdaptiveRoutingPlanSelector.Select(
            fixedPlan,
            recommendation,
            Contract(RiskLevel.R1),
            Environment.CurrentDirectory,
            new AdaptiveRoutingPolicy
            {
                Enabled = true,
                MinimumReadyRecords = 50,
                AllowedRisks = [RiskLevel.R1]
            });

        selected.Model.Should().Be("adaptive-model");
        selected.Budget.Should().BeSameAs(fixedPlan.Budget);
        selected.Verification.Should().BeSameAs(fixedPlan.Verification);
        selected.Capabilities.Should().BeSameAs(fixedPlan.Capabilities);
    }

    [Fact]
    public void RollbackInsufficientSampleOrDisallowedRisk_PreservesFixedPlan()
    {
        var fixedPlan = Plan("fixed-model");
        var recommendation = Recommendation("adaptive-model", matchingRecords: 49);

        AdaptiveRoutingPlanSelector.Select(
                fixedPlan,
                recommendation,
                Contract(RiskLevel.R1),
                Environment.CurrentDirectory,
                new AdaptiveRoutingPolicy { Enabled = true, RollbackRequested = true })
            .Should().BeSameAs(fixedPlan);
        AdaptiveRoutingPlanSelector.Select(
                fixedPlan,
                recommendation,
                Contract(RiskLevel.R1),
                Environment.CurrentDirectory,
                new AdaptiveRoutingPolicy { Enabled = true, MinimumReadyRecords = 50 })
            .Should().BeSameAs(fixedPlan);
        AdaptiveRoutingPlanSelector.Select(
                fixedPlan,
                Recommendation("adaptive-model", matchingRecords: 100),
                Contract(RiskLevel.R3),
                Environment.CurrentDirectory,
                new AdaptiveRoutingPolicy
                {
                    Enabled = true,
                    MinimumReadyRecords = 50,
                    AllowedRisks = [RiskLevel.R0, RiskLevel.R1]
                })
            .Should().BeSameAs(fixedPlan);
    }

    [Fact]
    public void CanaryMismatch_PreservesFixedPlan()
    {
        var fixedPlan = Plan("fixed-model");
        var otherRepository = Path.Combine(Path.GetTempPath(), $"aecs-canary-{Guid.NewGuid():N}");

        var selected = AdaptiveRoutingPlanSelector.Select(
            fixedPlan,
            Recommendation("adaptive-model", matchingRecords: 100),
            Contract(RiskLevel.R1),
            Environment.CurrentDirectory,
            new AdaptiveRoutingPolicy
            {
                Enabled = true,
                MinimumReadyRecords = 50,
                AllowedRisks = [RiskLevel.R1],
                CanaryRepositoryPath = otherRepository
            });

        selected.Should().BeSameAs(fixedPlan);
    }

    [Fact]
    public void BudgetExpansion_FailsClosed()
    {
        var fixedPlan = Plan("fixed-model");
        var recommendation = Recommendation(
            "adaptive-model",
            matchingRecords: 100,
            budget: new ExecutionBudget
            {
                MaxTokens = fixedPlan.Budget.MaxTokens + 1,
                MaxCostUsd = fixedPlan.Budget.MaxCostUsd,
                MaxRetries = fixedPlan.Budget.MaxRetries,
                MaxDurationSeconds = fixedPlan.Budget.MaxDurationSeconds,
                MaxFilesChanged = fixedPlan.Budget.MaxFilesChanged
            });

        var action = () => AdaptiveRoutingPlanSelector.Select(
            fixedPlan,
            recommendation,
            Contract(RiskLevel.R1),
            Environment.CurrentDirectory,
            new AdaptiveRoutingPolicy
            {
                Enabled = true,
                MinimumReadyRecords = 50,
                AllowedRisks = [RiskLevel.R1]
            });

        action.Should().Throw<InvalidOperationException>().WithMessage("*expand*budget*");
    }

    private static ExecutionPlan Plan(string model) => new()
    {
        TaskId = "T1",
        Model = model,
        Budget = new ExecutionBudget
        {
            MaxTokens = 1000,
            MaxCostUsd = 0.25m,
            MaxRetries = 1,
            MaxDurationSeconds = 120,
            MaxFilesChanged = 3
        },
        Risk = RiskLevel.R1,
        Verification = new VerificationProfile { Build = true },
        Capabilities = ExecutionCapabilityPolicy.RestrictiveDefault()
    };

    private static TaskContract Contract(RiskLevel risk) => new()
    {
        Id = "T1",
        Objective = "Fix a low risk bug",
        Constraints = new TaskConstraints { SecurityRisk = risk }
    };

    private static AdaptiveShadowRecommendation Recommendation(
        string model,
        int matchingRecords,
        ExecutionBudget? budget = null) => new()
        {
            DataStatus = AdaptiveShadowDataStatus.Ready,
            Inputs = new AdaptiveShadowInputs
            {
                Risk = RiskLevel.R1,
                TaskType = "bugfix",
                MatchingRecords = matchingRecords
            },
            RecommendedPlan = new AdaptiveShadowPlan
            {
                Model = model,
                Budget = budget ?? Plan("fixed-model").Budget,
                Verification = Plan("fixed-model").Verification,
                Capabilities = Plan("fixed-model").Capabilities
            }
        };
}
