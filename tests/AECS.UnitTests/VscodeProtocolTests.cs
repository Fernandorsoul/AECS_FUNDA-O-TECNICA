using System.Text.Json;
using AECS.Cli.Vscode;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class VscodeProtocolTests
{
    private const string Token = "vscode-session-token-with-at-least-32-characters";

    [Fact]
    public async Task Session_ProcessesVersionedAuthenticatedMainFlow()
    {
        var handler = new FlowHandler();
        var session = new VscodeProtocolSession(Token, handler);
        var input = new StringReader(string.Join(Environment.NewLine, new[]
        {
            Request("1", "initialize", new { }),
            Request("2", "execution.start", new { clientRequestId = Guid.NewGuid(), taskFile = "task.yaml" }),
            Request("3", "execution.get", new { operationId = FlowHandler.OperationId }),
            Request("4", "execution.inspect", new { operationId = FlowHandler.OperationId }),
            Request("5", "review.submit", new
            {
                operationId = FlowHandler.OperationId,
                expectedDiffHash = "sha256:abc",
                decision = "Approve",
                justification = "Reviewed locally",
                policyReference = "policy/test-v1",
                validMinutes = 15
            })
        }));
        var output = new StringWriter();

        await session.RunAsync(input, output, CancellationToken.None);

        var responses = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
        responses.Should().HaveCount(5);
        responses.Should().OnlyContain(response =>
            response.RootElement.GetProperty("protocol").GetString() == VscodeProtocolConstants.Version &&
            response.RootElement.GetProperty("ok").GetBoolean());
        handler.Methods.Should().Equal(
            "initialize",
            "execution.start",
            "execution.get",
            "execution.inspect",
            "review.submit");
        responses[^1].RootElement.GetProperty("result").GetProperty("persisted")
            .GetBoolean().Should().BeTrue();

        foreach (var response in responses)
            response.Dispose();
    }

    [Theory]
    [InlineData("aecs.vscode/v0", Token, "unsupported_protocol")]
    [InlineData(VscodeProtocolConstants.Version, "wrong-session-token-with-32-characters", "unauthorized")]
    public async Task Session_RejectsWrongVersionOrToken(
        string protocol,
        string token,
        string expectedCode)
    {
        var handler = new FlowHandler();
        var session = new VscodeProtocolSession(Token, handler);
        var line = JsonSerializer.Serialize(new
        {
            protocol,
            id = "request-1",
            method = "initialize",
            token,
            parameters = new { }
        });

        var response = await session.ProcessLineAsync(line, CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error!.Code.Should().Be(expectedCode);
        handler.Methods.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationStore_PreservesLinkAndMarksOrphanAsInterrupted()
    {
        var root = Path.Combine(Path.GetTempPath(), "aecs-vscode-tests", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        try
        {
            var store = new VscodeOperationStore(Path.Combine(root, "state"));
            var operation = new VscodeExecutionOperation
            {
                OperationId = Guid.NewGuid(),
                ClientRequestId = Guid.NewGuid(),
                RepositoryPath = repository,
                TaskFile = Path.Combine(repository, "task.yaml"),
                TaskId = "TASK-VSCODE",
                Status = VscodeOperationStatus.Running,
                EvidenceId = Guid.NewGuid(),
                OwnerProcessId = int.MaxValue,
                OwnerProcessStartedAt = DateTime.UnixEpoch,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await store.SaveAsync(operation, CancellationToken.None);

            await store.RecoverInterruptedAsync(repository, CancellationToken.None);

            var recovered = await store.LoadAsync(operation.OperationId, CancellationToken.None);
            recovered.Should().NotBeNull();
            recovered!.Status.Should().Be(VscodeOperationStatus.Interrupted);
            recovered.EvidenceId.Should().Be(operation.EvidenceId);
            (await store.FindByClientRequestAsync(
                operation.ClientRequestId,
                repository,
                CancellationToken.None)).Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string Request(string id, string method, object parameters) =>
        JsonSerializer.Serialize(new
        {
            protocol = VscodeProtocolConstants.Version,
            id,
            method,
            token = Token,
            parameters
        });

    private sealed class FlowHandler : IVscodeProtocolHandler
    {
        public static readonly Guid OperationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public List<string> Methods { get; } = [];

        public Task<object?> HandleAsync(
            string method,
            JsonElement parameters,
            CancellationToken cancellationToken)
        {
            Methods.Add(method);
            object result = method switch
            {
                "initialize" => new { protocol = VscodeProtocolConstants.Version },
                "execution.start" => new { operationId = OperationId, status = "Running" },
                "execution.get" => new { operationId = OperationId, status = "Completed" },
                "execution.inspect" => new
                {
                    operationId = OperationId,
                    diff = "diff --git a/a b/a",
                    gates = new[] { new { verifier = "Build", status = "Pass" } }
                },
                "review.submit" => new { status = "Approved", persisted = true },
                _ => throw new VscodeProtocolException("method_not_found", "Unknown method.")
            };
            return Task.FromResult<object?>(result);
        }
    }
}
