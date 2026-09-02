using System.Net;
using System.Text.Json;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class CloudAdapterTests
{
    [Fact]
    public void PricingCatalog_LoadsVersionedEmbeddedProviderRates()
    {
        var catalog = ProviderPricingCatalog.Current;
        var rate = catalog.Resolve("gpt-4o-mini");

        catalog.Table.SchemaVersion.Should().Be(ProviderPricingCatalog.SchemaVersion);
        catalog.Table.Version.Should().Be("2026-09-01");
        catalog.Table.Currency.Should().Be("USD");
        catalog.Table.Hash.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        rate.InputUsdPerMillionTokens.Should().Be(0.15m);
        rate.OutputUsdPerMillionTokens.Should().Be(0.60m);
        rate.EffectiveDate.Should().Be(new DateOnly(2024, 7, 18));
        rate.Source.Should().StartWith("https://openai.com/");
        catalog.Resolve("gpt-4o").InputUsdPerMillionTokens.Should().Be(2.50m);
        catalog.Resolve("gpt-4-turbo").OutputUsdPerMillionTokens.Should().Be(30m);
    }

    [Fact]
    public async Task ExecuteAsync_RecordsProviderUsageAndEstimateDivergence()
    {
        var response = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-audit-1",
            choices = new[]
            {
                new { message = new { content = "FILE: src/Changed.cs\n```csharp\nclass Changed {}\n```" } }
            },
            usage = new { prompt_tokens = 120, completion_tokens = 30 }
        });
        var adapter = new CloudAdapter(
            new HttpClient(new MockHttpMessageHandler(response, HttpStatusCode.OK)),
            new CloudAdapterOptions { ApiKey = "test-key", Model = "gpt-4o-mini" });

        var result = await adapter.ExecuteAsync(Request(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.UsageAccounting.Should().NotBeNull();
        result.UsageAccounting!.ProviderRequestId.Should().Be("chatcmpl-audit-1");
        result.UsageAccounting.ProviderInputTokens.Should().Be(120);
        result.UsageAccounting.ProviderOutputTokens.Should().Be(30);
        result.UsageAccounting.InputTokenDivergence.Should().NotBeNull();
        result.UsageAccounting.OutputTokenDivergence.Should().NotBeNull();
        result.UsageAccounting.RateCardUsageBasis.Should().Be("provider-reported");
        result.UsageAccounting.RateCardEstimatedCostUsd.Should().Be(0.000036m);
        result.UsageAccounting.AccountedCostBasis.Should().Be("rate-card-estimate");
    }

    [Fact]
    public async Task ExecuteAsync_MissingProviderUsage_RemainsExplicitlyMissing()
    {
        var response = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-no-usage",
            choices = new[]
            {
                new { message = new { content = "FILE: src/Changed.cs\n```csharp\nclass Changed {}\n```" } }
            }
        });
        var adapter = new CloudAdapter(
            new HttpClient(new MockHttpMessageHandler(response, HttpStatusCode.OK)),
            new CloudAdapterOptions { ApiKey = "test-key", Model = "gpt-4o-mini" });

        var result = await adapter.ExecuteAsync(Request(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.InputTokens.Should().BePositive();
        result.UsageAccounting!.ProviderInputTokens.Should().BeNull();
        result.UsageAccounting.ProviderOutputTokens.Should().BeNull();
        result.UsageAccounting.InputTokenDivergence.Should().BeNull();
        result.UsageAccounting.RateCardUsageBasis.Should().Be("client-estimated");
        result.UsageAccounting.RateCardEstimatedCostUsd.Should().BePositive();
    }

    [Fact]
    public async Task ExecuteAsync_PartialProviderUsage_DoesNotInventMissingTokenCount()
    {
        var response = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-partial-usage",
            choices = new[]
            {
                new { message = new { content = "FILE: src/Changed.cs\n```csharp\nclass Changed {}\n```" } }
            },
            usage = new { prompt_tokens = 80 }
        });
        var adapter = new CloudAdapter(
            new HttpClient(new MockHttpMessageHandler(response, HttpStatusCode.OK)),
            new CloudAdapterOptions { ApiKey = "test-key", Model = "gpt-4o-mini" });

        var result = await adapter.ExecuteAsync(Request(), CancellationToken.None);

        result.UsageAccounting!.ProviderInputTokens.Should().Be(80);
        result.UsageAccounting.ProviderOutputTokens.Should().BeNull();
        result.UsageAccounting.OutputTokenDivergence.Should().BeNull();
        result.UsageAccounting.RateCardUsageBasis.Should()
            .Be("provider-reported-with-client-fallback");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyChoicesStillAccountsForReportedUsage()
    {
        var response = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-empty",
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = 50, completion_tokens = 2 }
        });
        var adapter = new CloudAdapter(
            new HttpClient(new MockHttpMessageHandler(response, HttpStatusCode.OK)),
            new CloudAdapterOptions { ApiKey = "test-key", Model = "gpt-4o-mini" });

        var result = await adapter.ExecuteAsync(Request(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.InputTokens.Should().Be(50);
        result.OutputTokens.Should().Be(2);
        result.UsageAccounting!.CostComplete.Should().BeTrue();
        result.UsageAccounting.ProviderInputTokens.Should().Be(50);
        result.UsageAccounting.AccountedCostUsd.Should().Be(0.0000087m);
    }

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
        result.UsageAccounting!.RateCardEstimatedCostUsd.Should().Be(0m);
        result.UsageAccounting.RateCardUsageBasis.Should().Be("no-provider-request");
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
