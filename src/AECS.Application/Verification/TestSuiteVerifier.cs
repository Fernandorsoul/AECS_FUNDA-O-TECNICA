using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public sealed class TestSuiteVerifier : IVerifier
{
    public const string UnitName = "UnitTests";
    public const string IntegrationName = "IntegrationTests";
    public const string AcceptanceName = "AcceptanceTests";

    private readonly IProcessRunner _processRunner;
    private readonly TestSuiteCategory _category;
    private readonly TestSuiteCommandProfile _profile;
    private readonly Func<TimeSpan>? _remainingDuration;

    public TestSuiteVerifier(
        IProcessRunner processRunner,
        TestSuiteCategory category,
        TestSuiteCommandProfile profile,
        Func<TimeSpan>? remainingDuration = null)
    {
        _processRunner = processRunner;
        _category = category;
        _profile = profile;
        _remainingDuration = remainingDuration;
    }

    public string Name => NameFor(_category);
    public VerificationCategory Category => VerificationCategory.HighConfidence;

    public static string NameFor(TestSuiteCategory category) => category switch
    {
        TestSuiteCategory.Unit => UnitName,
        TestSuiteCategory.Integration => IntegrationName,
        TestSuiteCategory.Acceptance => AcceptanceName,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    public static string PhaseFor(TestSuiteCategory category, bool baseline) =>
        (category, baseline) switch
        {
            (TestSuiteCategory.Unit, true) => ExecutionCapabilityPhases.BaselineUnitTest,
            (TestSuiteCategory.Integration, true) => ExecutionCapabilityPhases.BaselineIntegrationTest,
            (TestSuiteCategory.Acceptance, true) => ExecutionCapabilityPhases.BaselineAcceptanceTest,
            (TestSuiteCategory.Unit, false) => ExecutionCapabilityPhases.CandidateUnitTest,
            (TestSuiteCategory.Integration, false) => ExecutionCapabilityPhases.CandidateIntegrationTest,
            (TestSuiteCategory.Acceptance, false) => ExecutionCapabilityPhases.CandidateAcceptanceTest,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        var resultsDirectory = string.Empty;
        try
        {
            var timeout = GetTimeout(context.Contract.Budget);
            if (timeout <= TimeSpan.Zero)
                return Error(context.AgentRunId, "Wall-clock budget exhausted before test suite ran");
            if (_profile.Mode == TestGateMode.Disabled)
                return Error(context.AgentRunId, "A disabled test suite cannot be executed");
            TestSuiteCommandGuard.ValidateArguments(_profile, _category);

            var execution = RepositoryExecutionProfileResolver.ResolveTestSuite(
                context.RepoPath,
                context.Contract.Execution,
                _profile);
            resultsDirectory = Path.Combine(
                context.RepoPath,
                ".aecs-verification",
                "test-results",
                context.Phase,
                _category.ToString().ToLowerInvariant());
            if (Directory.Exists(resultsDirectory))
                Directory.Delete(resultsDirectory, recursive: true);
            Directory.CreateDirectory(resultsDirectory);

            var arguments = execution.TestArguments.ToList();
            arguments.AddRange(_profile.Arguments);
            arguments.Add("--logger");
            arguments.Add("trx");
            arguments.Add("--results-directory");
            arguments.Add(resultsDirectory);
            var request = new ProcessExecutionRequest
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = execution.WorkingDirectory,
                Timeout = timeout,
                Phase = PhaseFor(
                    _category,
                    context.Phase.Equals("baseline", StringComparison.Ordinal))
            };
            var process = await _processRunner.RunAsync(request, cancellationToken);
            var command = ExecutionCommandEvidenceFactory.Create(context, request, process);
            context.CommandEvidence.Add(command);

            TestResultSummary summary;
            try
            {
                summary = TestResultSummaryReader.Read(resultsDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Xml.XmlException)
            {
                return Result(
                    context.AgentRunId,
                    VerificationStatus.Error,
                    Severity.Critical,
                    $"{Name} result discovery failed closed: {ex.Message}",
                    Evidence(command, new TestResultSummary(false, 0, 0, 0, 0, 0)));
            }

            var evidence = Evidence(command, summary);
            if (process.TimedOut || process.Cancelled)
            {
                return Result(
                    context.AgentRunId,
                    VerificationStatus.Error,
                    Severity.Critical,
                    process.TimedOut
                        ? $"{Name} timed out; process tree terminated"
                        : $"{Name} was cancelled; process tree terminated",
                    evidence);
            }
            if (process.ExitCode != 0)
            {
                return Result(
                    context.AgentRunId,
                    VerificationStatus.Fail,
                    Severity.Error,
                    $"{Name} exited with code {process.ExitCode} " +
                    $"({summary.Passed}/{summary.Executed} passed)",
                    evidence);
            }
            if (!summary.DiscoveryCompleted || summary.Executed == 0)
            {
                var required = _profile.Mode == TestGateMode.Required;
                return Result(
                    context.AgentRunId,
                    required ? VerificationStatus.Fail : VerificationStatus.Skip,
                    required ? Severity.Error : Severity.Warning,
                    $"{Name} exited successfully but executed zero tests",
                    evidence);
            }
            if (summary.Failed > 0 || summary.Passed != summary.Executed)
            {
                return Result(
                    context.AgentRunId,
                    VerificationStatus.Fail,
                    Severity.Error,
                    $"{Name} did not fully pass ({summary.Passed}/{summary.Executed})",
                    evidence);
            }

            return Result(
                context.AgentRunId,
                VerificationStatus.Pass,
                Severity.Info,
                $"{Name} passed ({summary.Passed}/{summary.Executed}; " +
                $"{summary.Discovered} discovered)",
                evidence);
        }
        catch (Exception ex)
        {
            return Error(context.AgentRunId, $"{Name} failed closed: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(resultsDirectory) && Directory.Exists(resultsDirectory))
            {
                try
                {
                    Directory.Delete(resultsDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The command evidence is already captured; cleanup of the disposable
                    // worktree remains the outer pipeline's final safety boundary.
                }
            }
        }
    }

    private TestSuiteEvidence Evidence(
        ExecutionCommandEvidence command,
        TestResultSummary summary) => new()
        {
            Category = _category,
            Mode = _profile.Mode,
            Target = _profile.Target.Replace('\\', '/'),
            Arguments = command.Arguments,
            DiscoveryCompleted = summary.DiscoveryCompleted,
            Discovered = summary.Discovered,
            Executed = summary.Executed,
            Passed = summary.Passed,
            Failed = summary.Failed,
            Skipped = summary.Skipped,
            CommandEvidenceId = command.Id
        };

    private VerificationResult Error(string agentRunId, string message) => Result(
        agentRunId,
        VerificationStatus.Error,
        Severity.Critical,
        message,
        new TestSuiteEvidence
        {
            Category = _category,
            Mode = _profile.Mode,
            Target = _profile.Target.Replace('\\', '/')
        });

    private VerificationResult Result(
        string agentRunId,
        VerificationStatus status,
        Severity severity,
        string message,
        TestSuiteEvidence evidence) => new()
        {
            AgentRunId = agentRunId,
            Verifier = Name,
            Status = status,
            Severity = severity,
            Message = message,
            TestSuite = evidence
        };

    private TimeSpan GetTimeout(ExecutionBudget budget)
    {
        var configured = TimeSpan.FromSeconds(Math.Min(
            Math.Max(1, budget.MaxDurationSeconds),
            Math.Max(1, _profile.TimeoutSeconds)));
        if (_remainingDuration is null)
            return configured;
        var remaining = _remainingDuration();
        return remaining < configured ? remaining : configured;
    }
}

internal static class TestSuiteCommandGuard
{
    public static void ValidateArguments(
        TestSuiteCommandProfile profile,
        TestSuiteCategory category)
    {
        if (profile.Arguments.Any(argument =>
                string.IsNullOrWhiteSpace(argument) ||
                argument.Any(char.IsControl) ||
                argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-l", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--logger", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--results-directory", StringComparison.OrdinalIgnoreCase) ||
                category == TestSuiteCategory.Acceptance &&
                argument.StartsWith("--filter", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{category} test suite contains an unsafe or controller-owned argument.");
        }
    }
}
