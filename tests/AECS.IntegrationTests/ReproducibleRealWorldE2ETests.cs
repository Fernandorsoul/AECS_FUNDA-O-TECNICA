using System.Diagnostics;
using System.Text.Json;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class ReproducibleRealWorldE2ETests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    [Trait("Category", "RealWorldE2E")]
    public async Task VersionedAgronomoPlusScenarios_ProduceExpectedDecisionsAndReport()
    {
        var expectationsPath = System.IO.Path.Combine(
            RealWorldFixtureRepository.FixturePath,
            "expected-results.json");
        var expectations = JsonSerializer.Deserialize<RealWorldExpectations>(
            await File.ReadAllTextAsync(expectationsPath),
            JsonOptions) ?? throw new InvalidOperationException("E2E expectations are invalid.");
        expectations.SchemaVersion.Should().Be("1.0");
        expectations.Scenarios.Should().HaveCountGreaterThanOrEqualTo(2);

        var configuredReportPath = Environment.GetEnvironmentVariable("AECS_E2E_REPORT_PATH");
        var preserveArtifacts = !string.IsNullOrWhiteSpace(configuredReportPath);
        var reportPath = preserveArtifacts
            ? System.IO.Path.GetFullPath(configuredReportPath!)
            : System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aecs-real-world-report-{Guid.NewGuid():N}",
                "report.json");
        var artifactRoot = System.IO.Path.GetDirectoryName(reportPath)!;
        Directory.CreateDirectory(artifactRoot);

        var reports = new List<RealWorldScenarioReport>();
        var failures = new List<string>();
        var suiteStopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var scenario in expectations.Scenarios)
            {
                var outcome = await RunScenarioAsync(scenario, artifactRoot);
                reports.Add(outcome.Report);
                failures.AddRange(outcome.Failures.Select(failure => $"{scenario.Id}: {failure}"));
            }
        }
        finally
        {
            suiteStopwatch.Stop();
            var report = new RealWorldSuiteReport
            {
                SchemaVersion = expectations.SchemaVersion,
                FixtureRevision = expectations.FixtureRevision,
                GeneratedAt = DateTime.UtcNow,
                Duration = suiteStopwatch.Elapsed,
                Succeeded = reports.Count == expectations.Scenarios.Count &&
                    reports.All(scenario => scenario.Succeeded),
                Scenarios = reports
            };
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, JsonOptions));
        }

        File.Exists(reportPath).Should().BeTrue();
        using (JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
        {
            // Parsing is the contract: CI consumers receive valid machine-readable JSON.
        }

        failures.Should().BeEmpty(
            $"the reproducible E2E report is available at {reportPath}");
        reports.Should().OnlyContain(report => report.Succeeded);

        if (!preserveArtifacts)
            Directory.Delete(artifactRoot, recursive: true);
    }

    private static async Task<RealWorldScenarioOutcome> RunScenarioAsync(
        RealWorldScenarioExpectation scenario,
        string artifactRoot)
    {
        var failures = new List<string>();
        var evidencePath = System.IO.Path.Combine(artifactRoot, "evidence", scenario.Id);
        var stopwatch = Stopwatch.StartNew();
        StagedExecutionResult? result = null;
        ExecutionEvidence? evidence = null;
        RealWorldRepositorySnapshot? before = null;
        RealWorldRepositorySnapshot? after = null;
        Exception? exception = null;
        var agent = new FixtureAgent(await File.ReadAllTextAsync(System.IO.Path.Combine(
            RealWorldFixtureRepository.FixturePath,
            scenario.Candidate)));

        await using (var repository = await RealWorldFixtureRepository.CreateAsync(evidencePath))
        {
            before = await repository.SnapshotAsync();
            try
            {
                var contract = new TaskContractParser().ParseFromFile(System.IO.Path.Combine(
                    repository.Path,
                    scenario.Contract));
                var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
                result = await new StagedExecutionPipeline(
                        agent,
                        repository.ProcessRunner,
                        store,
                        retryDelay: (_, _) => Task.CompletedTask)
                    .RunAsync(repository.Path, contract, CancellationToken.None);
                evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
            }
            catch (Exception caught)
            {
                exception = caught;
                failures.Add($"pipeline threw {caught.GetType().Name}: {caught.Message}");
            }
            finally
            {
                after = await repository.SnapshotAsync();
            }

            ValidateRepositoryIsolation(repository, before, after, agent, failures);
            if (result is not null)
                ValidateResult(repository, scenario, result, evidence, failures);
        }

        stopwatch.Stop();
        var report = CreateScenarioReport(
            scenario,
            result,
            evidence,
            before,
            after,
            exception,
            stopwatch.Elapsed,
            failures);
        return new RealWorldScenarioOutcome(report, failures);
    }

    private static void ValidateRepositoryIsolation(
        RealWorldFixtureRepository repository,
        RealWorldRepositorySnapshot before,
        RealWorldRepositorySnapshot after,
        FixtureAgent agent,
        List<string> failures)
    {
        Expect(string.IsNullOrEmpty(before.Status), "baseline checkout was dirty", failures);
        Expect(string.IsNullOrEmpty(after.Status), "original checkout became dirty", failures);
        Expect(after.Commit == before.Commit, "original checkout commit changed", failures);
        Expect(after.Branch == before.Branch, "original checkout branch changed", failures);
        Expect(before.Worktrees.Count == 1, "baseline had unexpected worktrees", failures);
        Expect(after.Worktrees.Count == 1, "temporary worktree leaked after execution", failures);
        Expect(
            after.Worktrees.Count == 1 && SamePath(after.Worktrees[0], repository.Path),
            "the remaining worktree is not the original checkout",
            failures);
        Expect(
            agent.WorkspacePaths.All(path => !Directory.Exists(path)),
            "an agent staging directory still exists",
            failures);
        Expect(
            !Directory.Exists(System.IO.Path.Combine(repository.Path, ".aecs-verification")),
            "targeted test artifacts leaked into the original checkout",
            failures);
    }

    private static void ValidateResult(
        RealWorldFixtureRepository repository,
        RealWorldScenarioExpectation scenario,
        StagedExecutionResult result,
        ExecutionEvidence? evidence,
        List<string> failures)
    {
        Expect(
            result.Decision.Decision.ToString() == scenario.ExpectedDecision,
            $"expected decision {scenario.ExpectedDecision}, received {result.Decision.Decision}",
            failures);
        Expect(
            result.FinalState.ToString() == scenario.ExpectedState,
            $"expected state {scenario.ExpectedState}, received {result.FinalState}",
            failures);
        Expect(result.OriginalRepositoryUnchanged, "pipeline did not attest original isolation", failures);
        Expect(
            result.BaselineVerificationResults.Count >= 2 &&
            result.BaselineVerificationResults.All(item => item.Status == VerificationStatus.Pass),
            "baseline build/tests did not both pass",
            failures);
        Expect(
            scenario.ExpectedChangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(result.CandidateChangeSet.ChangedFiles),
            "candidate changed files differ from the scenario oracle",
            failures);
        Expect(
            result.CandidateChangeSet.DiffHash.StartsWith("sha256:", StringComparison.Ordinal),
            "candidate diff hash is missing",
            failures);

        foreach (var verifier in scenario.ExpectedFailingVerifiers)
        {
            Expect(
                result.VerificationResults.Any(item =>
                    item.Verifier.Equals(verifier, StringComparison.OrdinalIgnoreCase) &&
                    item.Status != VerificationStatus.Pass),
                $"expected failing verifier {verifier} was not observed",
                failures);
        }

        if (scenario.RequireCandidateBuildAndTests)
        {
            Expect(
                result.VerificationResults.Any(item =>
                    item.Verifier == "Build" && item.Status == VerificationStatus.Pass),
                "candidate build did not pass",
                failures);
            Expect(
                result.VerificationResults.Any(item =>
                    item.Verifier == "Tests" && item.Status == VerificationStatus.Pass),
                "candidate tests did not pass",
                failures);
            Expect(
                result.AcceptanceCriteriaResults.Count >= 2 &&
                result.AcceptanceCriteriaResults.All(item => item.Status == VerificationStatus.Pass),
                "targeted acceptance evidence did not pass",
                failures);
            Expect(
                result.CandidateCommands.Any(command => command.Arguments.FirstOrDefault() == "build") &&
                result.CandidateCommands.Any(command => command.Arguments.FirstOrDefault() == "test"),
                "candidate command evidence lacks build or tests",
                failures);
        }

        Expect(evidence is not null, "persisted evidence could not be loaded", failures);
        if (evidence is not null)
        {
            Expect(
                evidence.Baseline.Commit == result.Baseline.Commit,
                "evidence baseline does not match the result",
                failures);
            Expect(
                evidence.CandidateChangeSet.DiffHash == result.CandidateChangeSet.DiffHash,
                "evidence diff hash does not match the result",
                failures);
            Expect(
                evidence.FinalDecision.Decision == result.Decision.Decision,
                "evidence decision does not match the result",
                failures);
        }

        Expect(File.Exists(result.EvidenceLocation), "evidence JSON was not persisted", failures);
        Expect(
            !IsWithin(result.EvidenceLocation, repository.Path),
            "evidence was written inside the target repository",
            failures);
    }

    private static RealWorldScenarioReport CreateScenarioReport(
        RealWorldScenarioExpectation expectation,
        StagedExecutionResult? result,
        ExecutionEvidence? evidence,
        RealWorldRepositorySnapshot? before,
        RealWorldRepositorySnapshot? after,
        Exception? exception,
        TimeSpan duration,
        IReadOnlyCollection<string> failures) => new()
        {
            Id = expectation.Id,
            ContractId = result?.Contract.Id ?? string.Empty,
            ExpectedDecision = expectation.ExpectedDecision,
            ActualDecision = result?.Decision.Decision.ToString() ?? "Error",
            ExpectedState = expectation.ExpectedState,
            ActualState = result?.FinalState.ToString() ?? "Error",
            Succeeded = failures.Count == 0,
            Error = exception?.ToString() ?? string.Empty,
            Failures = failures.ToList(),
            Duration = duration,
            BaselineCommit = result?.Baseline.Commit ?? before?.Commit ?? string.Empty,
            OriginalRepositoryUnchanged = result?.OriginalRepositoryUnchanged == true &&
                before?.Commit == after?.Commit &&
                before?.Branch == after?.Branch &&
                string.IsNullOrEmpty(after?.Status),
            WorktreesBefore = before?.Worktrees.ToList() ?? [],
            WorktreesAfter = after?.Worktrees.ToList() ?? [],
            ChangedFiles = result?.CandidateChangeSet.ChangedFiles.ToList() ?? [],
            DiffHash = result?.CandidateChangeSet.DiffHash ?? string.Empty,
            EvidenceId = result?.EvidenceId.ToString("N") ?? string.Empty,
            EvidenceLocation = result?.EvidenceLocation ?? string.Empty,
            EvidenceReloaded = evidence is not null,
            BaselineVerifications = result?.BaselineVerificationResults.Select(item =>
                new RealWorldVerificationReport(item.Verifier, item.Status.ToString(), item.Message)).ToList() ?? [],
            CandidateVerifications = result?.VerificationResults.Select(item =>
                new RealWorldVerificationReport(item.Verifier, item.Status.ToString(), item.Message)).ToList() ?? [],
            AcceptanceCriteria = result?.AcceptanceCriteriaResults.Select(item =>
                new RealWorldAcceptanceReport(item.CriterionId, item.Status.ToString(), item.Message)).ToList() ?? [],
            BaselineCommands = result?.BaselineCommands.Select(Command).ToList() ?? [],
            CandidateCommands = result?.CandidateCommands.Select(Command).ToList() ?? []
        };

    private static RealWorldCommandReport Command(ExecutionCommandEvidence command) => new(
        command.FileName,
        command.Arguments.ToList(),
        command.ExitCode,
        command.TimedOut,
        command.Cancelled,
        command.Duration);

    private static void Expect(bool condition, string message, ICollection<string> failures)
    {
        if (!condition)
            failures.Add(message);
    }

    private static bool SamePath(string left, string right) => string.Equals(
        System.IO.Path.GetFullPath(left).TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar),
        System.IO.Path.GetFullPath(right).TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static bool IsWithin(string candidate, string root)
    {
        var resolvedRoot = System.IO.Path.GetFullPath(root).TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        var resolvedCandidate = System.IO.Path.GetFullPath(candidate);
        return resolvedCandidate.StartsWith(
            resolvedRoot + System.IO.Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private sealed class FixtureAgent : IAgentAdapter
    {
        private readonly string _response;

        public FixtureAgent(string response)
        {
            _response = response;
        }

        public List<string> WorkspacePaths { get; } = [];

        public Task<AgentRunResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            WorkspacePaths.Add(request.RepoPath);
            return Task.FromResult(new AgentRunResult
            {
                Success = true,
                StdOut = _response,
                ExitCode = 0,
                ExitReason = "Completed",
                Duration = TimeSpan.FromMilliseconds(10),
                InputTokens = 100,
                OutputTokens = 100,
                EstimatedCost = 0.001m
            });
        }
    }
}

internal sealed class RealWorldExpectations
{
    public string SchemaVersion { get; init; } = string.Empty;
    public string FixtureRevision { get; init; } = string.Empty;
    public List<RealWorldScenarioExpectation> Scenarios { get; init; } = [];
}

internal sealed class RealWorldScenarioExpectation
{
    public string Id { get; init; } = string.Empty;
    public string Contract { get; init; } = string.Empty;
    public string Candidate { get; init; } = string.Empty;
    public string ExpectedDecision { get; init; } = string.Empty;
    public string ExpectedState { get; init; } = string.Empty;
    public List<string> ExpectedChangedFiles { get; init; } = [];
    public List<string> ExpectedFailingVerifiers { get; init; } = [];
    public bool RequireCandidateBuildAndTests { get; init; }
}

internal sealed class RealWorldSuiteReport
{
    public string SchemaVersion { get; init; } = string.Empty;
    public string FixtureRevision { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Succeeded { get; init; }
    public List<RealWorldScenarioReport> Scenarios { get; init; } = [];
}

internal sealed class RealWorldScenarioReport
{
    public string Id { get; init; } = string.Empty;
    public string ContractId { get; init; } = string.Empty;
    public string ExpectedDecision { get; init; } = string.Empty;
    public string ActualDecision { get; init; } = string.Empty;
    public string ExpectedState { get; init; } = string.Empty;
    public string ActualState { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Error { get; init; } = string.Empty;
    public List<string> Failures { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public string BaselineCommit { get; init; } = string.Empty;
    public bool OriginalRepositoryUnchanged { get; init; }
    public List<string> WorktreesBefore { get; init; } = [];
    public List<string> WorktreesAfter { get; init; } = [];
    public List<string> ChangedFiles { get; init; } = [];
    public string DiffHash { get; init; } = string.Empty;
    public string EvidenceId { get; init; } = string.Empty;
    public string EvidenceLocation { get; init; } = string.Empty;
    public bool EvidenceReloaded { get; init; }
    public List<RealWorldVerificationReport> BaselineVerifications { get; init; } = [];
    public List<RealWorldVerificationReport> CandidateVerifications { get; init; } = [];
    public List<RealWorldAcceptanceReport> AcceptanceCriteria { get; init; } = [];
    public List<RealWorldCommandReport> BaselineCommands { get; init; } = [];
    public List<RealWorldCommandReport> CandidateCommands { get; init; } = [];
}

internal sealed record RealWorldVerificationReport(string Verifier, string Status, string Message);
internal sealed record RealWorldAcceptanceReport(string CriterionId, string Status, string Message);
internal sealed record RealWorldCommandReport(
    string FileName,
    List<string> Arguments,
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Duration);

internal sealed record RealWorldScenarioOutcome(
    RealWorldScenarioReport Report,
    IReadOnlyList<string> Failures);
