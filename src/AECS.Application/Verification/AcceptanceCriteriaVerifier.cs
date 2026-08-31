using System.Xml.Linq;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public sealed class AcceptanceCriteriaVerificationOutcome
{
    public VerificationResult AggregateResult { get; init; } = new();
    public List<AcceptanceCriterionResult> Criteria { get; init; } = [];
}

public sealed class AcceptanceCriteriaVerifier
{
    public const string Name = "AcceptanceCriteria";
    private static readonly HashSet<string> StructuralVerifiers = new(
        [
            "AgentSuccess", "Application", "NonEmptyChange", "Scope", "Budget",
            "Build", "Tests", Name
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly IProcessRunner _processRunner;
    private readonly Func<TimeSpan>? _remainingDuration;

    public AcceptanceCriteriaVerifier(
        IProcessRunner processRunner,
        Func<TimeSpan>? remainingDuration = null)
    {
        _processRunner = processRunner;
        _remainingDuration = remainingDuration;
    }

    public async Task<AcceptanceCriteriaVerificationOutcome> VerifyAsync(
        VerificationContext context,
        IReadOnlyList<VerificationResult> verifierResults,
        bool canExecuteTests,
        CancellationToken cancellationToken)
    {
        var criteria = GetEffectiveCriteria(context.Contract);
        var results = new List<AcceptanceCriterionResult>();

        foreach (var criterion in criteria)
        {
            results.Add(criterion.Evidence.Type switch
            {
                AcceptanceEvidenceType.Verifier => VerifyWithVerifier(
                    criterion,
                    verifierResults),
                AcceptanceEvidenceType.Test => await VerifyWithTargetedTestAsync(
                    criterion,
                    context,
                    verifierResults,
                    canExecuteTests,
                    cancellationToken),
                _ => MissingEvidence(criterion, "No executable evidence is associated with this criterion")
            });
        }

        var requiredFailures = results
            .Where(result => result.Required && result.Status != VerificationStatus.Pass)
            .ToList();
        return new AcceptanceCriteriaVerificationOutcome
        {
            Criteria = results,
            AggregateResult = new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = requiredFailures.Count == 0
                    ? VerificationStatus.Pass
                    : VerificationStatus.Fail,
                Severity = requiredFailures.Count == 0 ? Severity.Info : Severity.Error,
                Message = requiredFailures.Count == 0
                    ? $"All {results.Count(result => result.Required)} required acceptance criteria have executable evidence"
                    : $"{requiredFailures.Count} required acceptance criterion/criteria lack passing evidence: " +
                        string.Join(", ", requiredFailures.Select(result => result.CriterionId))
            }
        };
    }

    public static List<AcceptanceCriterion> GetEffectiveCriteria(TaskContract contract)
    {
        var criteria = contract.AcceptanceRequirements.ToList();
        var mappedDescriptions = criteria
            .Select(criterion => criterion.Description)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var description in contract.AcceptanceCriteria)
        {
            if (mappedDescriptions.Contains(description))
                continue;

            criteria.Add(new AcceptanceCriterion
            {
                Id = $"AC-{criteria.Count + 1:000}",
                Description = description,
                Required = true
            });
            mappedDescriptions.Add(description);
        }

        return criteria;
    }

    private static AcceptanceCriterionResult VerifyWithVerifier(
        AcceptanceCriterion criterion,
        IReadOnlyList<VerificationResult> verifierResults)
    {
        if (string.IsNullOrWhiteSpace(criterion.Evidence.Reference))
            return MissingEvidence(criterion, "Verifier evidence requires an exact verifier name");
        if (criterion.Behavioral &&
            (!criterion.Evidence.EquivalentBehavioralEvidence ||
             IsStructuralVerifier(criterion.Evidence.Reference)))
        {
            return MissingEvidence(
                criterion,
                "Behavioral acceptance requires a changed targeted test or an explicitly equivalent non-structural verifier");
        }

        var matches = verifierResults.Where(result => string.Equals(
                result.Verifier,
                criterion.Evidence.Reference,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return MissingEvidence(
                criterion,
                $"Referenced verifier '{criterion.Evidence.Reference}' did not produce a result");
        if (matches.Count > 1)
            return MissingEvidence(
                criterion,
                $"Referenced verifier '{criterion.Evidence.Reference}' produced ambiguous duplicate results");

        var verifier = matches[0];
        return Result(
            criterion,
            verifier.Status == VerificationStatus.Pass
                ? VerificationStatus.Pass
                : VerificationStatus.Fail,
            verifier.Status == VerificationStatus.Pass
                ? $"Verifier '{verifier.Verifier}' passed"
                : $"Verifier '{verifier.Verifier}' did not pass: {verifier.Status} - {verifier.Message}",
            [$"verification-result:{verifier.Id:N}"]);
    }

    private async Task<AcceptanceCriterionResult> VerifyWithTargetedTestAsync(
        AcceptanceCriterion criterion,
        VerificationContext context,
        IReadOnlyList<VerificationResult> verifierResults,
        bool canExecuteTests,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(criterion.Evidence.Reference))
            return MissingEvidence(criterion, "Test evidence requires a non-empty dotnet test filter");
        if (!canExecuteTests)
            return MissingEvidence(criterion, "Targeted acceptance test was blocked by an earlier verification failure");

        var aggregateTests = verifierResults.Where(result => string.Equals(
                result.Verifier,
                "Tests",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (aggregateTests.Count != 1 || aggregateTests[0].Status != VerificationStatus.Pass)
        {
            return MissingEvidence(
                criterion,
                "Targeted acceptance evidence requires one passing Tests verifier result");
        }

        if (criterion.Behavioral && !HasChangedTestEvidence(criterion, context.CandidateChangeSet))
        {
            return MissingEvidence(
                criterion,
                "Behavioral acceptance requires the declared test file to be added or modified");
        }

        string resultsDirectory = string.Empty;
        try
        {
            var timeout = GetTimeout(context.Contract.Budget);
            if (timeout <= TimeSpan.Zero)
            {
                return Result(
                    criterion,
                    VerificationStatus.Fail,
                    "Wall-clock budget exhausted before targeted acceptance test",
                    [$"test-filter:{criterion.Evidence.Reference}"]);
            }
            var execution = RepositoryExecutionProfileResolver.Resolve(
                context.RepoPath,
                context.Contract.Execution);
            resultsDirectory = Path.Combine(
                context.RepoPath,
                ".aecs-verification",
                Sanitize(criterion.Id));
            Directory.CreateDirectory(resultsDirectory);

            var arguments = execution.TestArguments.ToList();
            arguments.Add("--filter");
            arguments.Add(criterion.Evidence.Reference);
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
                Phase = ExecutionCapabilityPhases.CandidateAcceptance
            };
            var executionResult = await _processRunner.RunAsync(request, cancellationToken);
            var commandEvidence = ExecutionCommandEvidenceFactory.Create(
                context,
                request,
                executionResult);
            context.CommandEvidence.Add(commandEvidence);
            var evidenceReferences = new List<string>
            {
                $"execution-command:{commandEvidence.Id:N}",
                $"test-filter:{criterion.Evidence.Reference}"
            };

            if (!executionResult.Succeeded)
            {
                var reason = executionResult.TimedOut
                    ? "timed out"
                    : executionResult.Cancelled
                        ? "was cancelled"
                        : $"exited with code {executionResult.ExitCode}";
                return Result(
                    criterion,
                    VerificationStatus.Fail,
                    $"Targeted acceptance test {reason}",
                    evidenceReferences);
            }

            var testResults = ReadTestResults(resultsDirectory);
            if (testResults.Executed == 0)
            {
                return Result(
                    criterion,
                    VerificationStatus.Fail,
                    "Test command exited successfully but the filter executed zero tests",
                    evidenceReferences);
            }
            if (testResults.Passed != testResults.Executed)
            {
                return Result(
                    criterion,
                    VerificationStatus.Fail,
                    $"Targeted acceptance test did not fully pass ({testResults.Passed}/{testResults.Executed})",
                    evidenceReferences);
            }

            return Result(
                criterion,
                VerificationStatus.Pass,
                $"Targeted acceptance test passed ({testResults.Passed}/{testResults.Executed})",
                [.. evidenceReferences, $"test-results:{testResults.Passed}/{testResults.Executed}"]);
        }
        catch (Exception ex)
        {
            return Result(
                criterion,
                VerificationStatus.Error,
                $"Targeted acceptance test evidence failed closed: {ex.Message}",
                [$"test-filter:{criterion.Evidence.Reference}"]);
        }
        finally
        {
            DeleteResultsDirectory(context.RepoPath, resultsDirectory);
        }
    }

    private static bool HasChangedTestEvidence(
        AcceptanceCriterion criterion,
        CandidateChangeSet candidate)
    {
        if (string.IsNullOrWhiteSpace(criterion.Evidence.TestPath))
            return false;

        var expectedPath = criterion.Evidence.TestPath.Replace('\\', '/');
        return LooksLikeTestPath(expectedPath) && candidate.ChangedFiles.Any(path =>
            string.Equals(path.Replace('\\', '/'), expectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeTestPath(string path) =>
        path.Split('/').Any(segment =>
            segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Tests", StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileNameWithoutExtension(path).EndsWith("Tests", StringComparison.OrdinalIgnoreCase);

    private static bool IsStructuralVerifier(string verifier) =>
        StructuralVerifiers.Contains(verifier);

    private static (int Executed, int Passed) ReadTestResults(string resultsDirectory)
    {
        var executed = 0;
        var passed = 0;
        foreach (var trxPath in Directory.EnumerateFiles(
                     resultsDirectory,
                     "*.trx",
                     SearchOption.AllDirectories))
        {
            var document = XDocument.Load(trxPath);
            var results = document.Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult")
                .ToList();
            executed += results.Count;
            passed += results.Count(element => string.Equals(
                element.Attribute("outcome")?.Value,
                "Passed",
                StringComparison.OrdinalIgnoreCase));
        }

        return (executed, passed);
    }

    private static AcceptanceCriterionResult MissingEvidence(
        AcceptanceCriterion criterion,
        string message) => Result(
        criterion,
        criterion.Required ? VerificationStatus.Fail : VerificationStatus.Skip,
        message,
        []);

    private static AcceptanceCriterionResult Result(
        AcceptanceCriterion criterion,
        VerificationStatus status,
        string message,
        List<string> evidenceReferences) => new()
        {
            CriterionId = criterion.Id,
            Description = criterion.Description,
            Required = criterion.Required,
            Behavioral = criterion.Behavioral,
            EvidenceType = criterion.Evidence.Type,
            EvidenceReference = criterion.Evidence.Reference,
            TestPath = criterion.Evidence.TestPath,
            Status = status,
            Message = message,
            EvidenceReferences = evidenceReferences
        };

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private TimeSpan GetTimeout(ExecutionBudget budget)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(1, budget.MaxDurationSeconds));
        if (_remainingDuration is null)
            return configured;
        var remaining = _remainingDuration();
        return remaining < configured ? remaining : configured;
    }

    private static void DeleteResultsDirectory(string workspacePath, string resultsDirectory)
    {
        if (string.IsNullOrWhiteSpace(resultsDirectory) || !Directory.Exists(resultsDirectory))
            return;

        var root = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(resultsDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Acceptance test results escaped the staging workspace.");

        try
        {
            Directory.Delete(target, recursive: true);
            var parent = Path.GetDirectoryName(target);
            if (parent is not null && Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
            // The enclosing disposable Git worktree remains authoritative and
            // will remove any runner-owned file that is still being released.
        }
        catch (UnauthorizedAccessException)
        {
            // Same fail-safe cleanup path as above; no file reaches the original checkout.
        }
    }
}
