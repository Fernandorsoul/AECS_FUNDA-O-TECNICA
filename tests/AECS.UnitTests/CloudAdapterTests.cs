using System.Net;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class CloudAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_429_ExposesRateLimitAndRetryAfter()
    {
        var handler = new MockHttpMessageHandler(
            "rate limited",
            HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromSeconds(7));
        var adapter = new CloudAdapter(
            new HttpClient(handler),
            new CloudAdapterOptions
            {
                ApiKey = "test-key",
                Model = "gpt-4o-mini",
                MaxTokens = 4096,
                Seed = 123
            });

        var result = await adapter.ExecuteAsync(
            Request(),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(429);
        result.ExitReason.Should().Be("RateLimited");
        result.FailureKind.Should().Be(AgentFailureKind.RateLimited);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
        handler.LastRequestContent.Should().Contain("\"seed\":123");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutRemainingCost_DoesNotCallProvider()
    {
        var handler = new MockHttpMessageHandler("", HttpStatusCode.OK);
        var adapter = new CloudAdapter(
            new HttpClient(handler),
            new CloudAdapterOptions { ApiKey = "test-key", Model = "gpt-4o-mini" });
        var request = Request(new ExecutionBudget
        {
            MaxTokens = 1000,
            MaxCostUsd = 0m,
            MaxRetries = 1,
            MaxDurationSeconds = 60,
            MaxFilesChanged = 10
        });

        var result = await adapter.ExecuteAsync(request, CancellationToken.None);

        result.FailureKind.Should().Be(AgentFailureKind.BudgetExceeded);
        handler.LastRequestContent.Should().BeNull();
    }

    private static AgentExecutionRequest Request(ExecutionBudget? budget = null) => new()
    {
        TaskId = "T-CLOUD",
        Objective = "Return a small change",
        RepoPath = Path.GetTempPath(),
        Budget = budget ?? new ExecutionBudget
        {
            MaxTokens = 10000,
            MaxCostUsd = 1m,
            MaxRetries = 1,
            MaxDurationSeconds = 60,
            MaxFilesChanged = 10
        },
        Model = "gpt-4o-mini"
    };
}
