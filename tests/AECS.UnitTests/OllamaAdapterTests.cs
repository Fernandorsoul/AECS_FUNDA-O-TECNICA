using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using FluentAssertions;

namespace AECS.UnitTests;

public class OllamaAdapterTests
{
    private static AgentExecutionRequest CreateRequest(
        string model = "codellama:3b",
        string contextPrompt = "") => new()
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
            Model = model,
            ContextPrompt = contextPrompt
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
    public async Task ExecuteAsync_IncludesCompiledRepositoryContextInProviderPrompt()
    {
        var json = JsonSerializer.Serialize(new
        {
            response = "FILE: src/New.cs\n```csharp\nclass New { }\n```",
            prompt_eval_count = 20,
            eval_count = 10,
            total_duration = 1L
        });
        var handler = new MockHttpMessageHandler(json, HttpStatusCode.OK);
        var adapter = new OllamaAdapter(
            new HttpClient(handler),
            "http://localhost:11434",
            seed: 321);
        const string context = "## REPOSITORY CONTEXT\n### src/Existing.cs\n" +
            "Symbols:\n- class Demo.Existing\n```csharp\nclass Existing { }\n```";

        await adapter.ExecuteAsync(
            CreateRequest(contextPrompt: context),
            CancellationToken.None);

        handler.LastRequestContent.Should().Contain("REPOSITORY CONTEXT");
        handler.LastRequestContent.Should().Contain("src/Existing.cs");
        handler.LastRequestContent.Should().Contain("class Demo.Existing");
        handler.LastRequestContent.Should().Contain("class Existing");
        handler.LastRequestContent.Should().Contain("\"seed\":321");
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
    private readonly TimeSpan? _retryAfter;

    public MockHttpMessageHandler(string responseContent, HttpStatusCode statusCode,
        bool throwOnSend = false, int delayMs = 0, TimeSpan? retryAfter = null)
    {
        _responseContent = responseContent;
        _statusCode = statusCode;
        _throwOnSend = throwOnSend;
        _delayMs = delayMs;
        _retryAfter = retryAfter;
    }

    public string? LastRequestContent { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_throwOnSend)
            throw new HttpRequestException("Connection refused");

        if (request.Content is not null)
            LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);

        if (_delayMs > 0)
            await Task.Delay(_delayMs, cancellationToken);

        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent)
        };
        if (_retryAfter.HasValue)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(_retryAfter.Value);
        return response;
    }
}
