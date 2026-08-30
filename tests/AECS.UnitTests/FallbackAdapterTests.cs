using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using FluentAssertions;

namespace AECS.UnitTests;

public class FallbackAdapterTests
{
    private static AgentExecutionRequest CreateRequest() => new()
    {
        TaskId = "T1",
        Objective = "Fix null handling",
        RepoPath = "/tmp/test",
        Scope = new() { Allowed = ["src/**"] },
        Budget = ExecutionBudget.Default,
        Model = "test-model"
    };

    [Fact]
    public async Task LocalSucceeds_WithFiles_DoesNotFallback()
    {
        var localResult = new AgentRunResult
        {
            Success = true,
            FilesChanged = ["src/Test.cs"],
            Duration = TimeSpan.FromSeconds(5),
            ExitReason = "Completed"
        };

        var local = new MockAgentAdapter { PresetResult = localResult };
        var cloud = new MockAgentAdapter { PresetResult = new AgentRunResult { Success = true, FilesChanged = ["src/Cloud.cs"] } };
        var fallback = new FallbackAdapter(local, cloud);

        var result = await fallback.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.FilesChanged.Should().Contain("src/Test.cs");
        result.FilesChanged.Should().NotContain("src/Cloud.cs");
        result.ExitReason.Should().Be("Completed");
    }

    [Fact]
    public async Task LocalSucceeds_WithoutFileTelemetry_DoesNotFallback()
    {
        var localResult = new AgentRunResult
        {
            Success = true,
            FilesChanged = [], // No files produced
            Duration = TimeSpan.FromSeconds(10),
            ExitReason = "Completed"
        };

        var cloudResult = new AgentRunResult
        {
            Success = true,
            FilesChanged = ["src/Fixed.cs"],
            Duration = TimeSpan.FromSeconds(3),
            InputTokens = 500,
            OutputTokens = 300,
            EstimatedCost = 0.01m,
            ExitReason = "Completed"
        };

        var local = new MockAgentAdapter { PresetResult = localResult };
        var cloud = new MockAgentAdapter { PresetResult = cloudResult };
        var fallback = new FallbackAdapter(local, cloud);

        var result = await fallback.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.FilesChanged.Should().BeEmpty();
        result.ExitReason.Should().Be("Completed");
        result.Duration.Should().Be(TimeSpan.FromSeconds(10));
        result.InputTokens.Should().Be(0);
        result.EstimatedCost.Should().Be(0m);
    }

    [Fact]
    public async Task LocalConnectionError_FallsBackToCloud()
    {
        var localResult = new AgentRunResult
        {
            Success = false,
            StdErr = "Connection refused",
            Duration = TimeSpan.FromSeconds(2),
            ExitReason = "ConnectionError"
        };

        var cloudResult = new AgentRunResult
        {
            Success = true,
            FilesChanged = ["src/Fixed.cs"],
            Duration = TimeSpan.FromSeconds(5),
            ExitReason = "Completed"
        };

        var local = new MockAgentAdapter { PresetResult = localResult };
        var cloud = new MockAgentAdapter { PresetResult = cloudResult };
        var fallback = new FallbackAdapter(local, cloud);

        var result = await fallback.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FilesChanged.Should().Contain("src/Fixed.cs");
        result.ExitReason.Should().Be("CompletedViaFallback");
    }

    [Fact]
    public async Task LocalTimeout_FallsBackToCloud()
    {
        var localResult = new AgentRunResult
        {
            Success = false,
            StdErr = "Timed out",
            Duration = TimeSpan.FromSeconds(120),
            ExitReason = "Cancelled"
        };

        var cloudResult = new AgentRunResult
        {
            Success = true,
            FilesChanged = ["src/Fixed.cs"],
            Duration = TimeSpan.FromSeconds(8),
            ExitReason = "Completed"
        };

        var local = new MockAgentAdapter { PresetResult = localResult };
        var cloud = new MockAgentAdapter { PresetResult = cloudResult };
        var fallback = new FallbackAdapter(local, cloud);

        var result = await fallback.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExitReason.Should().Be("CompletedViaFallback");
        result.Duration.Should().Be(TimeSpan.FromSeconds(128)); // 120 + 8
    }

    [Fact]
    public async Task BothFail_ReturnsCloudFailure()
    {
        var localResult = new AgentRunResult
        {
            Success = false,
            ExitReason = "ConnectionError",
            Duration = TimeSpan.FromSeconds(2)
        };

        var cloudResult = new AgentRunResult
        {
            Success = false,
            StdErr = "API key invalid",
            ExitReason = "ApiError",
            Duration = TimeSpan.FromSeconds(1)
        };

        var local = new MockAgentAdapter { PresetResult = localResult };
        var cloud = new MockAgentAdapter { PresetResult = cloudResult };
        var fallback = new FallbackAdapter(local, cloud);

        var result = await fallback.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ExitReason.Should().Be("ApiError");
    }

    [Fact]
    public async Task CustomFallbackCondition_Used()
    {
        // Custom condition: only fallback if duration > 50s
        var localResult = new AgentRunResult
        {
            Success = true,
            FilesChanged = ["src/Test.cs"],
            Duration = TimeSpan.FromSeconds(60),
            ExitReason = "Completed"
        };

        var cloudResult = new AgentRunResult
        {
            Success = true,
            FilesChanged = ["src/Cloud.cs"],
            Duration = TimeSpan.FromSeconds(5),
            ExitReason = "Completed"
        };

        var local = new MockAgentAdapter { PresetResult = localResult };
        var cloud = new MockAgentAdapter { PresetResult = cloudResult };
        var fallback = new FallbackAdapter(local, cloud, r => r.Duration.TotalSeconds > 50);

        var result = await fallback.ExecuteAsync(CreateRequest(), CancellationToken.None);

        // Custom condition triggered fallback even though local had files
        result.FilesChanged.Should().Contain("src/Cloud.cs");
        result.ExitReason.Should().Be("CompletedViaFallback");
    }
}
