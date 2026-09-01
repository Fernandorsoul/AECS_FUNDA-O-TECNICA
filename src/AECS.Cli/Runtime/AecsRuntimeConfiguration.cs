using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Enums;
using AECS.Infrastructure.Repositories;

namespace AECS.Cli.Runtime;

public static class AecsRuntimeConfigurationSchema
{
    public const string CurrentVersion = "aecs.runtime-config/v1";
    public const string EffectiveVersion = "aecs.runtime-effective/v1";
}

public sealed class AecsRuntimeConfigurationDocument
{
    public string SchemaVersion { get; init; } = string.Empty;
    public RuntimeAgentConfiguration? Agent { get; init; }
    public RuntimeEvidenceConfiguration? Evidence { get; init; }
    public RuntimeExecutionConfiguration? Execution { get; init; }
}

public sealed class RuntimeAgentConfiguration
{
    public string? Mode { get; init; }
    public RuntimeOllamaConfiguration? Ollama { get; init; }
    public RuntimeCloudFallbackConfiguration? CloudFallback { get; init; }
}

public sealed class RuntimeOllamaConfiguration
{
    public string? BaseUrl { get; init; }
    public int? ContextWindowTokens { get; init; }
}

public sealed class RuntimeCloudFallbackConfiguration
{
    public bool? Enabled { get; init; }
    public bool? AllowRepositoryContext { get; init; }
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public int? ContextWindowTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? CredentialEnvironmentVariable { get; init; }
    public List<string>? AllowedRisks { get; init; }
}

public sealed class RuntimeEvidenceConfiguration
{
    public string? Backend { get; init; }
    public string? JsonRoot { get; init; }
    public string? KeyDirectory { get; init; }
}

public sealed class RuntimeExecutionConfiguration
{
    public bool? AllowHostExecution { get; init; }
}

public sealed class EffectiveRuntimeSetting<T>
{
    public required T Value { get; init; }
    public string Source { get; init; } = "default";
}

public sealed class EffectiveSecretSetting
{
    public bool Configured { get; init; }
    public string Source { get; init; } = "unavailable";
}

public sealed class EffectiveAecsRuntimeConfiguration
{
    public string SchemaVersion { get; init; } =
        AecsRuntimeConfigurationSchema.EffectiveVersion;
    public string ConfigurationHash { get; internal set; } = string.Empty;
    public EffectiveRuntimeSetting<string> AgentMode { get; init; } = null!;
    public EffectiveRuntimeSetting<string> OllamaBaseUrl { get; init; } = null!;
    public EffectiveRuntimeSetting<int> OllamaContextWindowTokens { get; init; } = null!;
    public EffectiveRuntimeSetting<bool> CloudFallbackEnabled { get; init; } = null!;
    public EffectiveRuntimeSetting<bool> CloudRepositoryContextAllowed { get; init; } = null!;
    public EffectiveRuntimeSetting<string> CloudBaseUrl { get; init; } = null!;
    public EffectiveRuntimeSetting<string> CloudModel { get; init; } = null!;
    public EffectiveRuntimeSetting<int> CloudContextWindowTokens { get; init; } = null!;
    public EffectiveRuntimeSetting<int> CloudMaxOutputTokens { get; init; } = null!;
    public EffectiveRuntimeSetting<List<RiskLevel>> CloudAllowedRisks { get; init; } = null!;
    public EffectiveSecretSetting CloudCredential { get; init; } = new();
    public EffectiveRuntimeSetting<string> EvidenceBackend { get; init; } = null!;
    public EffectiveRuntimeSetting<string> EvidenceJsonRoot { get; init; } = null!;
    public EffectiveRuntimeSetting<string> EvidenceKeyDirectory { get; init; } = null!;
    public EffectiveSecretSetting PostgreSqlConnection { get; init; } = new();
    public EffectiveRuntimeSetting<bool> AllowHostExecution { get; init; } = null!;
    public string SandboxFactory { get; init; } = "docker-staged";
    public string ProfileAuthority { get; init; } = "task-contract";
    public string PolicyAuthority { get; init; } = "task-contract";
}

public sealed class ResolvedAecsRuntimeConfiguration
{
    public required EffectiveAecsRuntimeConfiguration Effective { get; init; }
    internal string CloudApiKey { get; init; } = string.Empty;
    internal string PostgreSqlConnectionString { get; init; } = string.Empty;
}

public static class AecsRuntimeConfigurationResolver
{
    private const string DefaultOllamaUrl = "http://localhost:11434";
    private const string DefaultCloudUrl = "https://api.openai.com/v1";
    private const string DefaultCloudModel = "gpt-4o-mini";
    private const string DefaultCloudCredentialVariable = "OPENAI_API_KEY";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ResolvedAecsRuntimeConfiguration Resolve(
        RuntimeCliOptions options,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        if (options.ParseError is not null)
            throw new InvalidOperationException(options.ParseError);

        var configuredPath = FirstNonEmpty(
            options.ConfigurationPath,
            getEnvironmentVariable("AECS_RUNTIME_CONFIG"));
        var document = configuredPath is null
            ? null
            : LoadDocument(configuredPath);
        var fileSource = configuredPath is null
            ? "default"
            : $"file:{Path.GetFullPath(configuredPath)}";

        var agentMode = Setting("local", "default");
        var ollamaUrl = Setting(DefaultOllamaUrl, "default");
        var ollamaContext = Setting(32_768, "default");
        var cloudEnabled = Setting(false, "default");
        var cloudContext = Setting(false, "default");
        var cloudUrl = Setting(DefaultCloudUrl, "default");
        var cloudModel = Setting(DefaultCloudModel, "default");
        var cloudContextWindow = Setting(32_768, "default");
        var cloudMaxOutput = Setting(4096, "default");
        var credentialVariable = Setting(DefaultCloudCredentialVariable, "default");
        var allowedRisks = Setting(new List<RiskLevel> { RiskLevel.R0, RiskLevel.R1 }, "default");
        var evidenceBackend = Setting("json", "default");
        var evidenceRoot = Setting(JsonExecutionEvidenceStore.GetDefaultRootPath(), "default");
        var keyDirectory = Setting(
            JsonExecutionEvidenceStore.GetDefaultKeyDirectoryPath(),
            "default");
        var allowHost = Setting(false, "default");

        if (document is not null)
        {
            agentMode = Apply(agentMode, document.Agent?.Mode, fileSource);
            ollamaUrl = Apply(ollamaUrl, document.Agent?.Ollama?.BaseUrl, fileSource);
            ollamaContext = Apply(
                ollamaContext,
                document.Agent?.Ollama?.ContextWindowTokens,
                fileSource);
            cloudEnabled = Apply(
                cloudEnabled,
                document.Agent?.CloudFallback?.Enabled,
                fileSource);
            cloudContext = Apply(
                cloudContext,
                document.Agent?.CloudFallback?.AllowRepositoryContext,
                fileSource);
            cloudUrl = Apply(
                cloudUrl,
                document.Agent?.CloudFallback?.BaseUrl,
                fileSource);
            cloudModel = Apply(
                cloudModel,
                document.Agent?.CloudFallback?.Model,
                fileSource);
            cloudContextWindow = Apply(
                cloudContextWindow,
                document.Agent?.CloudFallback?.ContextWindowTokens,
                fileSource);
            cloudMaxOutput = Apply(
                cloudMaxOutput,
                document.Agent?.CloudFallback?.MaxOutputTokens,
                fileSource);
            credentialVariable = Apply(
                credentialVariable,
                document.Agent?.CloudFallback?.CredentialEnvironmentVariable,
                fileSource);
            allowedRisks = ApplyRisks(
                allowedRisks,
                document.Agent?.CloudFallback?.AllowedRisks,
                fileSource);
            evidenceBackend = Apply(evidenceBackend, document.Evidence?.Backend, fileSource);
            evidenceRoot = Apply(evidenceRoot, document.Evidence?.JsonRoot, fileSource);
            keyDirectory = Apply(keyDirectory, document.Evidence?.KeyDirectory, fileSource);
            allowHost = Apply(
                allowHost,
                document.Execution?.AllowHostExecution,
                fileSource);
        }

        agentMode = ApplyEnvironment(agentMode, "AECS_AGENT_MODE", getEnvironmentVariable);
        ollamaUrl = ApplyEnvironment(ollamaUrl, "OLLAMA_BASE_URL", getEnvironmentVariable);
        ollamaContext = ApplyEnvironmentInt(
            ollamaContext,
            "AECS_OLLAMA_CONTEXT_WINDOW_TOKENS",
            getEnvironmentVariable);
        cloudEnabled = ApplyEnvironmentBool(
            cloudEnabled,
            "AECS_CLOUD_FALLBACK_ENABLED",
            getEnvironmentVariable);
        cloudContext = ApplyEnvironmentBool(
            cloudContext,
            "AECS_CLOUD_ALLOW_REPOSITORY_CONTEXT",
            getEnvironmentVariable);
        cloudUrl = ApplyEnvironmentWithAlias(
            cloudUrl,
            "OPENAI_BASE_URL",
            "ANTHROPIC_BASE_URL",
            getEnvironmentVariable);
        cloudModel = ApplyEnvironmentWithAlias(
            cloudModel,
            "OPENAI_MODEL",
            "ANTHROPIC_MODEL",
            getEnvironmentVariable);
        cloudContextWindow = ApplyEnvironmentInt(
            cloudContextWindow,
            "AECS_CLOUD_CONTEXT_WINDOW_TOKENS",
            getEnvironmentVariable);
        cloudMaxOutput = ApplyEnvironmentInt(
            cloudMaxOutput,
            "AECS_CLOUD_MAX_OUTPUT_TOKENS",
            getEnvironmentVariable);
        credentialVariable = ApplyEnvironment(
            credentialVariable,
            "AECS_CLOUD_CREDENTIAL_ENVIRONMENT_VARIABLE",
            getEnvironmentVariable);
        allowedRisks = ApplyEnvironmentRisks(
            allowedRisks,
            "AECS_CLOUD_ALLOWED_RISKS",
            getEnvironmentVariable);
        evidenceBackend = ApplyEnvironment(
            evidenceBackend,
            "AECS_EVIDENCE_STORE",
            getEnvironmentVariable);
        evidenceRoot = ApplyEnvironment(
            evidenceRoot,
            "AECS_EVIDENCE_PATH",
            getEnvironmentVariable);
        keyDirectory = ApplyEnvironment(
            keyDirectory,
            "AECS_EVIDENCE_KEY_DIRECTORY",
            getEnvironmentVariable);
        allowHost = ApplyEnvironmentBool(
            allowHost,
            "AECS_ALLOW_HOST_EXECUTION",
            getEnvironmentVariable);

        agentMode = Apply(agentMode, options.AgentMode, "flag:--mock");
        ollamaUrl = Apply(ollamaUrl, options.OllamaBaseUrl, "flag:--ollama-url");
        ollamaContext = Apply(
            ollamaContext,
            options.OllamaContextWindowTokens,
            "flag:--ollama-context-window");
        cloudEnabled = Apply(
            cloudEnabled,
            options.CloudFallbackEnabled,
            "flag:--enable-cloud-fallback");
        cloudContext = Apply(
            cloudContext,
            options.CloudRepositoryContextAllowed,
            "flag:--allow-cloud-context");
        cloudUrl = Apply(cloudUrl, options.CloudUrl, "flag:--cloud-url");
        cloudModel = Apply(cloudModel, options.CloudModel, "flag:--cloud-model");
        credentialVariable = Apply(
            credentialVariable,
            options.CloudCredentialEnvironmentVariable,
            "flag:--cloud-key-env");
        allowedRisks = ApplyRisks(
            allowedRisks,
            options.CloudAllowedRisks,
            "flag:--cloud-risks");
        evidenceBackend = Apply(
            evidenceBackend,
            options.EvidenceBackend,
            "flag:--evidence-store");
        evidenceRoot = Apply(
            evidenceRoot,
            options.EvidenceJsonRoot,
            "flag:--evidence-root");
        keyDirectory = Apply(
            keyDirectory,
            options.EvidenceKeyDirectory,
            "flag:--key-directory");
        allowHost = Apply(
            allowHost,
            options.AllowHostExecution,
            "flag:--allow-host-execution");

        agentMode = Setting(agentMode.Value.Trim().ToLowerInvariant(), agentMode.Source);
        evidenceBackend = Setting(
            evidenceBackend.Value.Trim().ToLowerInvariant() switch
            {
                "postgresql" => "postgres",
                var value => value
            },
            evidenceBackend.Source);

        var cloudSecret = ResolveCloudSecret(
            options,
            credentialVariable.Value,
            getEnvironmentVariable);
        var postgresSecret = getEnvironmentVariable(
            PostgreSqlExecutionEvidenceStore.ConnectionStringEnvironmentVariable);
        var postgresSecretSource = string.IsNullOrWhiteSpace(postgresSecret)
            ? "unavailable"
            : $"environment:{PostgreSqlExecutionEvidenceStore.ConnectionStringEnvironmentVariable}";

        Validate(
            agentMode.Value,
            ollamaUrl.Value,
            ollamaContext.Value,
            cloudEnabled.Value,
            cloudContext.Value,
            cloudUrl.Value,
            cloudModel.Value,
            cloudContextWindow.Value,
            cloudMaxOutput.Value,
            credentialVariable.Value,
            cloudSecret.Value,
            allowedRisks.Value,
            evidenceBackend.Value,
            evidenceRoot.Value,
            evidenceRoot.Source,
            keyDirectory.Value,
            postgresSecret);

        var effective = new EffectiveAecsRuntimeConfiguration
        {
            AgentMode = agentMode,
            OllamaBaseUrl = ollamaUrl,
            OllamaContextWindowTokens = ollamaContext,
            CloudFallbackEnabled = cloudEnabled,
            CloudRepositoryContextAllowed = cloudContext,
            CloudBaseUrl = cloudUrl,
            CloudModel = cloudModel,
            CloudContextWindowTokens = cloudContextWindow,
            CloudMaxOutputTokens = cloudMaxOutput,
            CloudAllowedRisks = allowedRisks,
            CloudCredential = new EffectiveSecretSetting
            {
                Configured = !string.IsNullOrWhiteSpace(cloudSecret.Value),
                Source = cloudSecret.Source
            },
            EvidenceBackend = evidenceBackend,
            EvidenceJsonRoot = evidenceRoot,
            EvidenceKeyDirectory = keyDirectory,
            PostgreSqlConnection = new EffectiveSecretSetting
            {
                Configured = !string.IsNullOrWhiteSpace(postgresSecret),
                Source = postgresSecretSource
            },
            AllowHostExecution = allowHost
        };
        effective.ConfigurationHash = Fingerprint(effective);
        return new ResolvedAecsRuntimeConfiguration
        {
            Effective = effective,
            CloudApiKey = cloudSecret.Value ?? string.Empty,
            PostgreSqlConnectionString = postgresSecret ?? string.Empty
        };
    }

    public static string ToJson(EffectiveAecsRuntimeConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, OutputOptions);

    public static string ToText(EffectiveAecsRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var cloud = configuration.CloudFallbackEnabled.Value
            ? $"enabled model={configuration.CloudModel.Value} risks=" +
              string.Join(',', configuration.CloudAllowedRisks.Value)
            : "disabled";
        return
            $"[AECS] runtime={configuration.SchemaVersion} hash={configuration.ConfigurationHash}\n" +
            $"[AECS] agent={configuration.AgentMode.Value} " +
            $"source={configuration.AgentMode.Source} ollama={configuration.OllamaBaseUrl.Value} " +
            $"ollama-source={configuration.OllamaBaseUrl.Source}\n" +
            $"[AECS] cloud={cloud} enabled-source={configuration.CloudFallbackEnabled.Source} " +
            $"model-source={configuration.CloudModel.Source} " +
            $"context-policy-source={configuration.CloudRepositoryContextAllowed.Source} " +
            $"risk-source={configuration.CloudAllowedRisks.Source} " +
            $"credential={(configuration.CloudCredential.Configured ? "configured" : "unavailable")} " +
            $"credential-source={configuration.CloudCredential.Source}\n" +
            $"[AECS] evidence={configuration.EvidenceBackend.Value} " +
            $"source={configuration.EvidenceBackend.Source} sandbox={configuration.SandboxFactory} " +
            $"host-execution={configuration.AllowHostExecution.Value} " +
            $"profiles={configuration.ProfileAuthority} policies={configuration.PolicyAuthority}\n";
    }

    private static AecsRuntimeConfigurationDocument LoadDocument(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Runtime configuration file was not found.", fullPath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            RejectDuplicateProperties(document.RootElement);
            var configuration = document.RootElement.Deserialize<AecsRuntimeConfigurationDocument>(
                    FileOptions)
                ?? throw new InvalidOperationException("Runtime configuration is empty.");
            if (!string.Equals(
                    configuration.SchemaVersion,
                    AecsRuntimeConfigurationSchema.CurrentVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unsupported runtime configuration schema '{configuration.SchemaVersion}'.");
            }
            return configuration;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Runtime configuration is invalid: {ex.Message}",
                ex);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException(
                        $"Runtime configuration contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static void Validate(
        string agentMode,
        string ollamaUrl,
        int ollamaContext,
        bool cloudEnabled,
        bool cloudContext,
        string cloudUrl,
        string cloudModel,
        int cloudContextWindow,
        int cloudMaxOutput,
        string credentialVariable,
        string? cloudSecret,
        IReadOnlyCollection<RiskLevel> allowedRisks,
        string evidenceBackend,
        string evidenceRoot,
        string evidenceRootSource,
        string keyDirectory,
        string? postgresConnection)
    {
        if (agentMode is not ("local" or "mock"))
            throw new InvalidOperationException("Agent mode must be 'local' or 'mock'.");
        ValidateEndpoint(ollamaUrl, "Ollama", allowNonTlsLoopback: true);
        if (ollamaContext < 1024)
            throw new InvalidOperationException("Ollama context window must be at least 1024 tokens.");
        ValidateEndpoint(cloudUrl, "Cloud fallback", allowNonTlsLoopback: true);
        if (string.IsNullOrWhiteSpace(cloudModel))
            throw new InvalidOperationException("Cloud fallback model is required.");
        if (cloudContextWindow < 1024 || cloudMaxOutput <= 0 || cloudMaxOutput >= cloudContextWindow)
        {
            throw new InvalidOperationException(
                "Cloud context/output token limits are invalid.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                credentialVariable,
                "^[A-Z_][A-Z0-9_]*$"))
        {
            throw new InvalidOperationException(
                "Cloud credential environment variable name is invalid.");
        }
        if (cloudEnabled)
        {
            if (agentMode == "mock")
                throw new InvalidOperationException("Mock agent mode cannot enable cloud fallback.");
            if (!cloudContext)
            {
                throw new InvalidOperationException(
                    "Cloud fallback requires explicit repository-context authorization.");
            }
            if (string.IsNullOrWhiteSpace(cloudSecret))
                throw new InvalidOperationException("Cloud fallback credential is unavailable.");
            if (allowedRisks.Count == 0)
                throw new InvalidOperationException("Cloud fallback requires at least one allowed risk.");
        }
        if (evidenceBackend is not ("json" or "postgres"))
            throw new InvalidOperationException("Evidence backend must be 'json' or 'postgres'.");
        if (string.IsNullOrWhiteSpace(keyDirectory))
            throw new InvalidOperationException("Evidence key directory is required.");
        if (evidenceBackend == "json" && string.IsNullOrWhiteSpace(evidenceRoot))
            throw new InvalidOperationException("JSON evidence root is required.");
        if (evidenceBackend == "postgres" && evidenceRootSource != "default")
        {
            throw new InvalidOperationException(
                "JSON evidence root cannot be configured for the PostgreSQL backend.");
        }
        if (evidenceBackend == "postgres" && string.IsNullOrWhiteSpace(postgresConnection))
        {
            throw new InvalidOperationException(
                $"PostgreSQL evidence requires the secret environment variable " +
                $"{PostgreSqlExecutionEvidenceStore.ConnectionStringEnvironmentVariable}.");
        }
    }

    private static void ValidateEndpoint(
        string value,
        string description,
        bool allowNonTlsLoopback)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != "https" && !(allowNonTlsLoopback && uri.IsLoopback)))
        {
            throw new InvalidOperationException(
                $"{description} endpoint must be HTTPS or an HTTP loopback URL without credentials.");
        }
    }

    private static (string? Value, string Source) ResolveCloudSecret(
        RuntimeCliOptions options,
        string variable,
        Func<string, string?> getEnvironmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(options.CloudKey))
            return (options.CloudKey, "flag:--cloud-key (redacted)");
        var value = getEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
            return (value, $"environment:{variable}");
        if (variable == DefaultCloudCredentialVariable)
        {
            value = getEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrWhiteSpace(value))
                return (value, "environment:ANTHROPIC_API_KEY");
        }
        return (null, $"environment:{variable} (unavailable)");
    }

    private static string Fingerprint(EffectiveAecsRuntimeConfiguration configuration)
    {
        configuration.ConfigurationHash = string.Empty;
        var json = JsonSerializer.Serialize(configuration, OutputOptions);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static EffectiveRuntimeSetting<T> Setting<T>(T value, string source) => new()
    {
        Value = value,
        Source = source
    };

    private static EffectiveRuntimeSetting<string> Apply(
        EffectiveRuntimeSetting<string> current,
        string? value,
        string source) => string.IsNullOrWhiteSpace(value)
            ? current
            : Setting(value.Trim(), source);

    private static EffectiveRuntimeSetting<int> Apply(
        EffectiveRuntimeSetting<int> current,
        int? value,
        string source) => value.HasValue ? Setting(value.Value, source) : current;

    private static EffectiveRuntimeSetting<bool> Apply(
        EffectiveRuntimeSetting<bool> current,
        bool? value,
        string source) => value.HasValue ? Setting(value.Value, source) : current;

    private static EffectiveRuntimeSetting<List<RiskLevel>> ApplyRisks(
        EffectiveRuntimeSetting<List<RiskLevel>> current,
        IReadOnlyCollection<string>? values,
        string source) => values is null
            ? current
            : Setting(ParseRisks(values), source);

    private static EffectiveRuntimeSetting<string> ApplyEnvironment(
        EffectiveRuntimeSetting<string> current,
        string name,
        Func<string, string?> getEnvironmentVariable) =>
        Apply(current, getEnvironmentVariable(name), $"environment:{name}");

    private static EffectiveRuntimeSetting<string> ApplyEnvironmentWithAlias(
        EffectiveRuntimeSetting<string> current,
        string primary,
        string alias,
        Func<string, string?> getEnvironmentVariable)
    {
        var primaryValue = getEnvironmentVariable(primary);
        return !string.IsNullOrWhiteSpace(primaryValue)
            ? Setting(primaryValue.Trim(), $"environment:{primary}")
            : Apply(current, getEnvironmentVariable(alias), $"environment:{alias}");
    }

    private static EffectiveRuntimeSetting<int> ApplyEnvironmentInt(
        EffectiveRuntimeSetting<int> current,
        string name,
        Func<string, string?> getEnvironmentVariable)
    {
        var value = getEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return current;
        if (!int.TryParse(value, out var parsed))
            throw new InvalidOperationException($"Environment variable {name} must be an integer.");
        return Setting(parsed, $"environment:{name}");
    }

    private static EffectiveRuntimeSetting<bool> ApplyEnvironmentBool(
        EffectiveRuntimeSetting<bool> current,
        string name,
        Func<string, string?> getEnvironmentVariable)
    {
        var value = getEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return current;
        var parsed = value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => throw new InvalidOperationException(
                $"Environment variable {name} must be true or false.")
        };
        return Setting(parsed, $"environment:{name}");
    }

    private static EffectiveRuntimeSetting<List<RiskLevel>> ApplyEnvironmentRisks(
        EffectiveRuntimeSetting<List<RiskLevel>> current,
        string name,
        Func<string, string?> getEnvironmentVariable)
    {
        var value = getEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? current
            : Setting(
                ParseRisks(value.Split(',', StringSplitOptions.RemoveEmptyEntries)),
                $"environment:{name}");
    }

    private static List<RiskLevel> ParseRisks(IEnumerable<string> values)
    {
        var risks = new List<RiskLevel>();
        foreach (var value in values)
        {
            if (!Enum.TryParse<RiskLevel>(value.Trim(), ignoreCase: true, out var risk) ||
                !Enum.IsDefined(risk))
            {
                throw new InvalidOperationException($"Unknown cloud fallback risk '{value}'.");
            }
            if (!risks.Contains(risk))
                risks.Add(risk);
        }
        return risks;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
