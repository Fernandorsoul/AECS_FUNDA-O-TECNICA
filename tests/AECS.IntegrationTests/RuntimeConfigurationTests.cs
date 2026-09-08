using AECS.Cli.Jarvis;
using AECS.Cli.Runtime;
using AECS.Domain.Enums;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class RuntimeConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"aecs-runtime-config-tests-{Guid.NewGuid():N}");

    public RuntimeConfigurationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void FileEnvironmentAndFlags_AreResolvedInDocumentedOrderWithOrigins()
    {
        var configPath = WriteConfiguration("""
            {
              "schemaVersion": "aecs.runtime-config/v1",
              "agent": {
                "mode": "local",
                "ollama": {
                  "baseUrl": "http://localhost:1111",
                  "contextWindowTokens": 4096
                }
              },
              "evidence": {
                "backend": "json",
                "jsonRoot": "file-evidence"
              },
              "execution": { "allowHostExecution": false }
            }
            """);
        var options = Parse(
            "--runtime-config", configPath,
            "--mock",
            "--ollama-url", "http://localhost:3333",
            "--evidence-root", Path.Combine(_root, "flag-evidence"),
            "--allow-host-execution");
        var environment = EnvironmentOf(
            ("OLLAMA_BASE_URL", "http://localhost:2222"),
            ("AECS_EVIDENCE_PATH", Path.Combine(_root, "env-evidence")));

        var resolved = AecsRuntimeConfigurationResolver.Resolve(options, environment);

        resolved.Effective.AgentMode.Value.Should().Be("mock");
        resolved.Effective.AgentMode.Source.Should().Be("flag:--mock");
        resolved.Effective.OllamaBaseUrl.Value.Should().Be("http://localhost:3333");
        resolved.Effective.OllamaBaseUrl.Source.Should().Be("flag:--ollama-url");
        resolved.Effective.OllamaContextWindowTokens.Value.Should().Be(4096);
        resolved.Effective.OllamaContextWindowTokens.Source.Should().StartWith("file:");
        resolved.Effective.EvidenceJsonRoot.Value.Should().EndWith("flag-evidence");
        resolved.Effective.AllowHostExecution.Value.Should().BeTrue();
    }

    [Fact]
    public void EffectiveConfiguration_RedactsCredentialAndIsSharedWithJarvis()
    {
        const string secret = "must-never-appear-in-output";
        var configPath = WriteConfiguration("""
            {
              "schemaVersion": "aecs.runtime-config/v1",
              "agent": {
                "cloudFallback": {
                  "enabled": true,
                  "allowRepositoryContext": true,
                  "baseUrl": "https://cloud.example.invalid/v1",
                  "model": "example-coder",
                  "credentialEnvironmentVariable": "AECS_TEST_CLOUD_KEY",
                  "allowedRisks": ["R0", "R2"]
                }
              },
              "evidence": {
                "jsonRoot": "EVIDENCE_ROOT",
                "keyDirectory": "KEY_ROOT"
              }
            }
            """
            .Replace("EVIDENCE_ROOT", Escape(Path.Combine(_root, "evidence")))
            .Replace("KEY_ROOT", Escape(Path.Combine(_root, "keys"))));
        var resolved = AecsRuntimeConfigurationResolver.Resolve(
            Parse("--runtime-config", configPath),
            EnvironmentOf(("AECS_TEST_CLOUD_KEY", secret)));

        using var runtime = AecsExecutionRuntime.Create(resolved);
        var jarvis = new JarvisRepl(_root, runtime);
        var json = AecsRuntimeConfigurationResolver.ToJson(runtime.Configuration);
        var text = AecsRuntimeConfigurationResolver.ToText(runtime.Configuration);

        runtime.AgentAdapter.Should().BeOfType<FallbackAdapter>();
        jarvis.RuntimeConfiguration.Should().BeSameAs(runtime.Configuration);
        runtime.Configuration.CloudCredential.Configured.Should().BeTrue();
        runtime.Configuration.CloudAllowedRisks.Value.Should().Equal(RiskLevel.R0, RiskLevel.R2);
        json.Should().Contain("aecs.runtime-effective/v1").And.NotContain(secret);
        text.Should().Contain("credential=configured").And.NotContain(secret);
    }

    [Theory]
    [InlineData(false, null, "explicit repository-context authorization")]
    [InlineData(true, null, "credential is unavailable")]
    [InlineData(true, "secret", "endpoint must be HTTPS")]
    public void InvalidCloudPolicy_FailsBeforeRuntimeExecution(
        bool allowContext,
        string? secret,
        string expected)
    {
        var endpoint = allowContext && secret is not null
            ? "http://cloud.example.invalid/v1"
            : "https://cloud.example.invalid/v1";
        var configPath = WriteConfiguration($$"""
            {
              "schemaVersion": "aecs.runtime-config/v1",
              "agent": {
                "cloudFallback": {
                  "enabled": true,
                  "allowRepositoryContext": {{allowContext.ToString().ToLowerInvariant()}},
                  "baseUrl": "{{endpoint}}",
                  "credentialEnvironmentVariable": "AECS_TEST_CLOUD_KEY"
                }
              }
            }
            """);
        var environment = EnvironmentOf(("AECS_TEST_CLOUD_KEY", secret));

        var action = () => AecsRuntimeConfigurationResolver.Resolve(
            Parse("--runtime-config", configPath),
            environment);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void UnknownOrDuplicateConfigurationProperties_FailClosed()
    {
        var unknown = WriteConfiguration("""
            {
              "schemaVersion": "aecs.runtime-config/v1",
              "apiKey": "forbidden"
            }
            """, "unknown.json");
        var duplicate = WriteConfiguration("""
            {
              "schemaVersion": "aecs.runtime-config/v1",
              "schemaVersion": "aecs.runtime-config/v1"
            }
            """, "duplicate.json");

        var unknownAction = () => AecsRuntimeConfigurationResolver.Resolve(
            Parse("--runtime-config", unknown),
            _ => null);
        var duplicateAction = () => AecsRuntimeConfigurationResolver.Resolve(
            Parse("--runtime-config", duplicate),
            _ => null);

        unknownAction.Should().Throw<InvalidOperationException>();
        duplicateAction.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate property*");
    }

    [Fact]
    public void AdaptiveRoutingPolicy_IsOptInRedactedAndFailClosed()
    {
        var configPath = WriteConfiguration("""
            {
              "schemaVersion": "aecs.runtime-config/v1",
              "execution": {
                "adaptiveRouting": {
                  "enabled": true,
                  "rollbackRequested": true,
                  "minimumReadyRecords": 75,
                  "canaryRepositoryPath": "CANARY_ROOT",
                  "allowedRisks": ["R0", "R1"]
                }
              }
            }
            """
            .Replace("CANARY_ROOT", Escape(Path.Combine(_root, "canary"))));

        var resolved = AecsRuntimeConfigurationResolver.Resolve(
            Parse("--runtime-config", configPath),
            _ => null);
        var text = AecsRuntimeConfigurationResolver.ToText(resolved.Effective);

        resolved.Effective.AdaptiveRoutingEnabled.Value.Should().BeTrue();
        resolved.Effective.AdaptiveRoutingRollbackRequested.Value.Should().BeTrue();
        resolved.Effective.AdaptiveRoutingMinimumReadyRecords.Value.Should().Be(75);
        resolved.Effective.AdaptiveRoutingAllowedRisks.Value.Should()
            .Equal(RiskLevel.R0, RiskLevel.R1);
        text.Should().Contain("adaptive-routing=enabled")
            .And.Contain("rollback=True")
            .And.Contain("minimum-ready-records=75");
    }

    [Fact]
    public void InvalidAdaptiveRoutingPolicy_FailsBeforeRuntimeExecution()
    {
        var configPath = WriteConfiguration("""
            {
              "schemaVersion": "aecs.runtime-config/v1",
              "execution": {
                "adaptiveRouting": {
                  "enabled": true,
                  "minimumReadyRecords": 1,
                  "allowedRisks": ["R0"]
                }
              }
            }
            """);

        var action = () => AecsRuntimeConfigurationResolver.Resolve(
            Parse("--runtime-config", configPath),
            _ => null);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*minimum ready records*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string WriteConfiguration(string json, string name = "runtime.json")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static RuntimeCliOptions Parse(params string[] args)
    {
        var options = new RuntimeCliOptions();
        for (var index = 0; index < args.Length; index++)
            options.TryConsume(args, ref index).Should().BeTrue();
        options.ParseError.Should().BeNull();
        return options;
    }

    private static Func<string, string?> EnvironmentOf(
        params (string Name, string? Value)[] variables)
    {
        var values = variables.ToDictionary(
            item => item.Name,
            item => item.Value,
            StringComparer.Ordinal);
        return name => values.GetValueOrDefault(name);
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");
}
