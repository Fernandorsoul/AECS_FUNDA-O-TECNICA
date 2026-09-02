using System.Text.Json;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class SecurityScanVerifierTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        $"aecs-security-scan-{Guid.NewGuid():N}");

    public SecurityScanVerifierTests()
    {
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task RealisticSecret_IsBlockedWithoutPersistingTheValue()
    {
        const string secret = "AKIA1234567890ABCDEF";
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "Credentials.cs"),
            $"internal static class Credentials {{ private const string Key = \"{secret}\"; }}");

        var result = await Verifier().VerifyAsync(
            Context(),
            CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        var finding = result.SecurityScan!.Findings.Should().ContainSingle().Which;
        finding.Rule.Should().Be("AECS-SECRET-AWS-ACCESS-KEY");
        finding.Severity.Should().Be(Severity.Critical);
        finding.Disposition.Should().Be(SecurityFindingDisposition.New);
        JsonSerializer.Serialize(result).Should().NotContain(secret);
        finding.Message.Should().NotContain(secret);
    }

    [Fact]
    public async Task BaselineDebt_IsRecordedButDoesNotBlockUnchangedCandidate()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "legacy.env"),
            "password=\"legacy-placeholder-value\"");
        var context = Context();
        var baseline = await new SecurityScanVerifier(
            new RecordingRunner(),
            SecurityScanVerifier.CreateDefaultScanners(),
            isBaseline: true).VerifyAsync(context, CancellationToken.None);
        var fingerprints = baseline.SecurityScan!.Findings
            .Select(finding => finding.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        var candidate = await new SecurityScanVerifier(
            new RecordingRunner(),
            SecurityScanVerifier.CreateDefaultScanners(),
            fingerprints).VerifyAsync(context, CancellationToken.None);

        baseline.Status.Should().Be(VerificationStatus.Pass);
        baseline.SecurityScan.Findings.Should().OnlyContain(finding =>
            finding.Disposition == SecurityFindingDisposition.Baseline);
        candidate.Status.Should().Be(VerificationStatus.Pass);
        candidate.SecurityScan!.Findings.Should().OnlyContain(finding =>
            finding.Disposition == SecurityFindingDisposition.Baseline);
    }

    [Fact]
    public async Task JustifiedExactSuppression_IsAuditableAndDoesNotBlock()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "example.env"),
            "api_key=\"documented-placeholder-value\"");
        var policy = new SecurityScanPolicy
        {
            Scanners = [SecurityScannerIds.Secrets],
            Suppressions =
            [
                new SecurityScanSuppression
                {
                    Rule = "AECS-SECRET-CREDENTIAL-ASSIGNMENT",
                    Path = "example.env",
                    Justification = "Documented non-production placeholder used by the fixture."
                }
            ]
        };

        var result = await new SecurityScanVerifier(new RecordingRunner()).VerifyAsync(
            Context(policy),
            CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Pass);
        var finding = result.SecurityScan!.Findings.Should().ContainSingle().Which;
        finding.Disposition.Should().Be(SecurityFindingDisposition.Suppressed);
        finding.SuppressionJustification.Should().Contain("non-production placeholder");
    }

    [Fact]
    public async Task CriticalSecurityPattern_IsNormalizedAndBlocked()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "UnsafeHttp.cs"),
            "handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;");

        var result = await new SecurityScanVerifier(new RecordingRunner()).VerifyAsync(
            Context(Policy(SecurityScannerIds.Patterns)),
            CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.SecurityScan!.Findings.Should().ContainSingle(finding =>
            finding.Rule == "AECS-PATTERN-TLS-BYPASS" &&
            finding.Severity == Severity.Critical &&
            finding.Path == "UnsafeHttp.cs");
    }

    [Fact]
    public async Task VulnerableDependency_UsesStructuredSandboxCommandAndSanitizesOutput()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var runner = new RecordingRunner(new ProcessExecutionResult
        {
            ExitCode = 0,
            StandardOutput = """
                An issue was encountered verifying workloads. For more information, run "dotnet workload update".
                {
                  "version": 1,
                  "projects": [{
                    "frameworks": [{
                      "topLevelPackages": [{
                        "id": "Newtonsoft.Json",
                        "resolvedVersion": "12.0.3"
                      }]
                    }]
                  }]
                }
                """,
            StandardError = "https://user:password@example.invalid password=super-secret-value",
            Environment = new ExecutionEnvironmentEvidence { Runtime = "docker" }
        });
        var context = Context(Policy(SecurityScannerIds.Dependencies));
        context = new VerificationContext
        {
            TaskId = context.TaskId,
            AgentRunId = context.AgentRunId,
            RepoPath = context.RepoPath,
            Contract = new TaskContract
            {
                Execution = new RepositoryExecutionProfile { Target = "Fixture.csproj" },
                Verification = context.Contract.Verification
            },
            CommandEvidence = context.CommandEvidence,
            Phase = "candidate"
        };

        var result = await new SecurityScanVerifier(runner).VerifyAsync(
            context,
            CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.SecurityScan!.VulnerabilityDatabaseVersion.Should()
            .Be(SecurityScanSchema.VulnerabilityDatabaseVersion);
        result.SecurityScan.Findings.Should().ContainSingle(finding =>
            finding.Rule == "GHSA-5crp-9r3c-p9vr" &&
            finding.Category == "vulnerable-dependency");
        runner.Requests.Should().ContainSingle();
        runner.Requests[0].FileName.Should().Be("dotnet");
        runner.Requests[0].Arguments.Should().Equal(
            "list", "Fixture.csproj", "package", "--include-transitive",
            "--format", "json");
        runner.Requests[0].Phase.Should().Be(ExecutionCapabilityPhases.CandidateSecurityScan);
        context.CommandEvidence.Should().ContainSingle();
        JsonSerializer.Serialize(context.CommandEvidence).Should()
            .NotContain("super-secret-value")
            .And.NotContain("user:password@");
    }

    [Fact]
    public async Task DependencyToolFailure_IsInconclusiveAndFailsClosed()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var runner = new RecordingRunner(new ProcessExecutionResult
        {
            ExitCode = 7,
            StandardError = "tool unavailable",
            Environment = new ExecutionEnvironmentEvidence { Runtime = "docker" }
        });
        var policy = Policy(SecurityScannerIds.Dependencies);
        var context = new VerificationContext
        {
            AgentRunId = "run",
            RepoPath = _workspace,
            Contract = new TaskContract
            {
                Execution = new RepositoryExecutionProfile { Target = "Fixture.csproj" },
                Verification = new VerificationProfile
                {
                    SecurityScan = true,
                    SecurityPolicy = policy
                }
            },
            CommandEvidence = [],
            Phase = "baseline"
        };

        var result = await new SecurityScanVerifier(runner, isBaseline: true).VerifyAsync(
            context,
            CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Error);
        result.Severity.Should().Be(Severity.Critical);
        result.Message.Should().Contain("inconclusive").And.Contain("exited with code 7");
        result.SecurityScan!.Scanners.Should().ContainSingle(scanner => !scanner.Conclusive);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private SecurityScanVerifier Verifier() => new(
        new RecordingRunner(),
        SecurityScanVerifier.CreateDefaultScanners());

    private VerificationContext Context(SecurityScanPolicy? policy = null) => new()
    {
        AgentRunId = "run",
        RepoPath = _workspace,
        Contract = new TaskContract
        {
            Verification = new VerificationProfile
            {
                SecurityScan = true,
                SecurityPolicy = policy ?? Policy(SecurityScannerIds.Secrets)
            }
        },
        CommandEvidence = [],
        Phase = "candidate"
    };

    private static SecurityScanPolicy Policy(string scanner) => new()
    {
        Scanners = [scanner]
    };

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly Queue<ProcessExecutionResult> _results;

        public RecordingRunner(params ProcessExecutionResult[] results)
        {
            _results = new Queue<ProcessExecutionResult>(results);
        }

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
