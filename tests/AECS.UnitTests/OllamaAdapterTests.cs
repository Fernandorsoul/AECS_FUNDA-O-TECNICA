using System.Net;
using System.Text.Json;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using FluentAssertions;

namespace AECS.UnitTests;

public class OllamaAdapterTests
{
    private static AgentExecutionRequest CreateRequest(string model = "codellama:3b") => new()
    {
        TaskId = "T1",
        Objective = "Fix null handling in CustomerMapper",
        AcceptanceCriteria = ["No null exceptions", "Tests pass"],
        RepoPath = "/tmp/test",
        Scope = new ScopeDefinition
        {
            Allowed = ["src/Customers/**", "tests/Customers/**"],
            Forbidden = ["src/Billing/**"]
        },
        Budget = ExecutionBudget.Default,
        Risk = RiskLevel.R1,
        Model = model
    };

    [Fact]
    public async Task ExecuteAsync_SuccessfulResponse_ReturnsResult()
    {
        var ollamaResponse = new
        {
            response = "FILE: src/Customers/CustomerMapper.cs\n```csharp\npublic class CustomerMapper { }\n```",
            prompt_eval_count = 150,
            eval_count = 200,
            total_duration = 5000000000L
        };

        var json = JsonSerializer.Serialize(ollamaResponse);
        var handler = new MockHttpMessageHandler(json, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var adapter = new OllamaAdapter(httpClient, "http://localhost:11434");

        var result = await adapter.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.InputTokens.Should().Be(150);
        result.OutputTokens.Should().Be(200);
        result.EstimatedCost.Should().Be(0m);
        result.FilesChanged.Should().Contain("src/Customers/CustomerMapper.cs");
        result.ExitReason.Should().Be("Completed");
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionError_ReturnsFailure()
    {
        var handler = new MockHttpMessageHandler("", HttpStatusCode.OK, throwOnSend: true);
        var httpClient = new HttpClient(handler);
        var adapter = new OllamaAdapter(httpClient, "http://localhost:11434");

        var result = await adapter.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ExitReason.Should().Be("ConnectionError");
        result.StdErr.Should().Contain("Failed to connect");
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_ReturnsCancelledResult()
    {
        var handler = new MockHttpMessageHandler("", HttpStatusCode.OK, delayMs: 5000);
        var httpClient = new HttpClient(handler);
        var adapter = new OllamaAdapter(httpClient, "http://localhost:11434");

        using var cts = new CancellationTokenSource(100);
        var result = await adapter.ExecuteAsync(CreateRequest(), cts.Token);

        result.Success.Should().BeFalse();
        result.ExitReason.Should().Be("Cancelled");
    }

    [Fact]
    public async Task ExecuteAsync_ExtractsMultipleFiles()
    {
        var response = """
            FILE: src/Customers/CustomerMapper.cs
            ```csharp
            public class CustomerMapper { }
            ```
            FILE: tests/Customers/CustomerMapperTests.cs
            ```csharp
            public class CustomerMapperTests { }
            ```
            """;

        var ollamaResponse = new
        {
            response,
            prompt_eval_count = 100,
            eval_count = 150,
            total_duration = 3000000000L
        };

        var json = JsonSerializer.Serialize(ollamaResponse);
        var handler = new MockHttpMessageHandler(json, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var adapter = new OllamaAdapter(httpClient, "http://localhost:11434");

        var result = await adapter.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FilesChanged.Should().HaveCount(2);
        result.FilesChanged.Should().Contain("src/Customers/CustomerMapper.cs");
        result.FilesChanged.Should().Contain("tests/Customers/CustomerMapperTests.cs");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResponse_ReturnsEmptyFiles()
    {
        var ollamaResponse = new
        {
            response = "",
            prompt_eval_count = 50,
            eval_count = 0,
            total_duration = 1000000000L
        };

        var json = JsonSerializer.Serialize(ollamaResponse);
        var handler = new MockHttpMessageHandler(json, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var adapter = new OllamaAdapter(httpClient, "http://localhost:11434");

        var result = await adapter.ExecuteAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FilesChanged.Should().BeEmpty();
    }
}

internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseContent;
    private readonly HttpStatusCode _statusCode;
    private readonly bool _throwOnSend;
    private readonly int _delayMs;

    public MockHttpMessageHandler(string responseContent, HttpStatusCode statusCode,
        bool throwOnSend = false, int delayMs = 0)
    {
        _responseContent = responseContent;
        _statusCode = statusCode;
        _throwOnSend = throwOnSend;
        _delayMs = delayMs;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_throwOnSend)
            throw new HttpRequestException("Connection refused");

        if (_delayMs > 0)
            await Task.Delay(_delayMs, cancellationToken);

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent)
        };
    }
}
