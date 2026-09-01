using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AECS.Cli.Vscode;

public static class VscodeProtocolConstants
{
    public const string Version = "aecs.vscode/v1";
    public const string TokenEnvironmentVariable = "AECS_VSCODE_SESSION_TOKEN";
    public const int MaximumMessageCharacters = 1024 * 1024;
}

public sealed class VscodeProtocolRequest
{
    public string Protocol { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public JsonElement Parameters { get; init; }
}

public sealed class VscodeProtocolError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class VscodeProtocolResponse
{
    public string Protocol { get; init; } = VscodeProtocolConstants.Version;
    public string Id { get; init; } = string.Empty;
    public bool Ok { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VscodeProtocolError? Error { get; init; }
}

public sealed class VscodeProtocolException : Exception
{
    public VscodeProtocolException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

public interface IVscodeProtocolHandler
{
    Task<object?> HandleAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken);
}

public sealed class VscodeProtocolSession
{
    private readonly string _sessionToken;
    private readonly IVscodeProtocolHandler _handler;
    private readonly JsonSerializerOptions _jsonOptions;

    public VscodeProtocolSession(string sessionToken, IVscodeProtocolHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        if (sessionToken.Length < 32)
            throw new ArgumentException("The VS Code session token must contain at least 32 characters.", nameof(sessionToken));
        _sessionToken = sessionToken;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
                return;

            var response = await ProcessLineAsync(line, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(response, _jsonOptions));
            await output.FlushAsync(cancellationToken);
        }
    }

    public async Task<VscodeProtocolResponse> ProcessLineAsync(
        string line,
        CancellationToken cancellationToken)
    {
        if (line.Length > VscodeProtocolConstants.MaximumMessageCharacters)
            return Failure(string.Empty, "message_too_large", "Protocol message exceeds the 1 MiB limit.");

        VscodeProtocolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<VscodeProtocolRequest>(line, _jsonOptions);
        }
        catch (JsonException)
        {
            return Failure(string.Empty, "invalid_json", "Protocol message is not valid JSON.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Id))
            return Failure(string.Empty, "invalid_request", "A non-empty request id is required.");
        if (!string.Equals(request.Protocol, VscodeProtocolConstants.Version, StringComparison.Ordinal))
            return Failure(request.Id, "unsupported_protocol", $"Expected protocol '{VscodeProtocolConstants.Version}'.");
        if (!TokenMatches(request.Token))
            return Failure(request.Id, "unauthorized", "The local protocol session could not be authenticated.");
        if (string.IsNullOrWhiteSpace(request.Method))
            return Failure(request.Id, "invalid_request", "A method is required.");

        try
        {
            var result = await _handler.HandleAsync(
                request.Method,
                request.Parameters,
                cancellationToken);
            return new VscodeProtocolResponse
            {
                Id = request.Id,
                Ok = true,
                Result = result
            };
        }
        catch (VscodeProtocolException ex)
        {
            return Failure(request.Id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(
                request.Id,
                "internal_error",
                "The AECS backend failed closed while processing the request.");
        }
    }

    private bool TokenMatches(string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(_sessionToken));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static VscodeProtocolResponse Failure(string id, string code, string message) => new()
    {
        Id = id,
        Ok = false,
        Error = new VscodeProtocolError { Code = code, Message = message }
    };
}
