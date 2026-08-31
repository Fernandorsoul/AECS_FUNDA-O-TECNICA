using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Replay;

public sealed class ExecutionReplayService
{
    private static readonly HashSet<string> SupportedCandidateGates = new(
        [
            "AgentSuccess", "Application", "NonEmptyChange", "Scope", "Budget",
            "Build", "Tests", "EB001-Architecture", "EB002-Pattern",
            "EB003-BreakingChange", "EB004-MissingChange",
            "EB005-HistoricalConflict", AcceptanceCriteriaVerifier.Name
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly IProcessRunner _processRunner;
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly GitWorkspaceManager _workspaceManager;
    private readonly IStagedProcessRunnerFactory _stagedProcessRunnerFactory;

    public ExecutionReplayService(
        IProcessRunner processRunner,
        IExecutionEvidenceStore evidenceStore,
        IStagedProcessRunnerFactory? stagedProcessRunnerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        _processRunner = processRunner;
        _evidenceStore = evidenceStore;
        _workspaceManager = new GitWorkspaceManager(processRunner);
        _stagedProcessRunnerFactory = stagedProcessRunnerFactory ??
            new DefaultStagedProcessRunnerFactory(processRunner);
    }

    public async Task<ExecutionReplayResult> ReplayAsync(
        ExecutionReplayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EvidenceId == Guid.Empty)
            throw new ArgumentException("A non-empty evidence ID is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RepositoryPath))
            throw new ArgumentException("A repository path is required.", nameof(request));

        var startedAt = DateTime.UtcNow;
        var repositoryPath = Path.GetFullPath(request.RepositoryPath);
        _evidenceStore.EnsureRepositoryIsolation(repositoryPath);
        var original = await _evidenceStore.LoadAsync(request.EvidenceId, cancellationToken)
            ?? throw new FileNotFoundException(
                $"Execution evidence '{request.EvidenceId:N}' was not found.");

        ReplayRun run;
        BaselineSnapshot? checkoutSnapshot = null;
        if (!PathsEqual(repositoryPath, original.Baseline.RepositoryPath))
        {
            run = ReplayRun.Failed(
                ExecutionReplayOutcome.Failed,
                "Requested repository does not match the repository authenticated by the evidence.");
        }
        else
        {
            try
            {
                checkoutSnapshot = await _workspaceManager.CaptureBaselineAsync(
                    repositoryPath,
                    cancellationToken);
                run = await ExecuteCoreAsync(
                    original,
                    repositoryPath,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                run = ReplayRun.Failed(
                    ExecutionReplayOutcome.Failed,
                    $"Replay failed closed: {ex.Message}");
            }
            finally
            {
                if (checkoutSnapshot is not null)
                {
                    await _workspaceManager.EnsureBaselineUnchangedAsync(
                        checkoutSnapshot,
                        CancellationToken.None);
                }
            }
        }

        var replay = new ExecutionReplayEvidence
        {
            ExecutionEvidenceId = original.Id,
            CandidateId = original.CandidateChangeSet.Id,
            Outcome = run.Outcome,
            RepositoryPath = original.Baseline.RepositoryPath,
            RequestedRepositoryPath = repositoryPath,
            BaselineCommit = original.Baseline.Commit,
            ExpectedDiffHash = original.CandidateChangeSet.DiffHash,
            ActualDiffHash = run.ActualDiffHash,
            Tools = run.Tools,
            Commands = run.Commands,
            Gates = run.Gates,
            AcceptanceCriteria = run.AcceptanceCriteria,
            Message = run.Message,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow
        };
        await _evidenceStore.AppendReplayAsync(
            original.Id,
            replay,
            CancellationToken.None);

        return new ExecutionReplayResult
        {
            Outcome = replay.Outcome,
            Message = replay.Message,
            Evidence = replay
        };
    }

    private async Task<ReplayRun> ExecuteCoreAsync(
        ExecutionEvidence original,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (!IsGitObjectId(original.Baseline.Commit))
        {
            return ReplayRun.Failed(
                ExecutionReplayOutcome.Failed,
                "Authenticated baseline commit is not a valid Git object ID.");
        }

        var baselineExists = await _processRunner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = ["cat-file", "-e", $"{original.Baseline.Commit}^{{commit}}"],
            WorkingDirectory = repositoryPath,
            Timeout = TimeSpan.FromSeconds(30)
        }, cancellationToken);
        if (!baselineExists.Succeeded)
        {
            return ReplayRun.Failed(
                ExecutionReplayOutcome.BaselineUnavailable,
                $"Baseline commit '{original.Baseline.Commit}' is unavailable in the requested repository.");
        }

        var persistedBaseline = new BaselineSnapshot
        {
            Commit = original.Baseline.Commit,
            Branch = original.Baseline.Branch,
            GitStatus = string.Empty,
            RepositoryPath = repositoryPath,
            CapturedAt = original.Baseline.CapturedAt
        };
        await using var workspace = await _workspaceManager.CreateWorkspaceAsync(
            persistedBaseline,
            cancellationToken);
        var stagedProcessRunner = await _stagedProcessRunnerFactory.CreateAsync(
            workspace.Path,
            ReplayExecutionProfile(original),
            cancellationToken);

        var baselineCommands = new List<ExecutionCommandEvidence>();
        var baselineContext = CreateContext(
            original,
            workspace.Path,
            original.CandidateChangeSet,
            baselineCommands);
        await ToolVersionProbe.CaptureAsync(
            stagedProcessRunner,
            baselineContext,
            cancellationToken);
        var baselineResults = await VerifyBaselineAsync(
            stagedProcessRunner,
            baselineContext,
            cancellationToken);

        var patchApplied = await ApplyAuthenticatedDiffAsync(
            workspace.Path,
            original.CandidateChangeSet.Diff,
            cancellationToken);
        if (!patchApplied.Succeeded)
        {
            var baselineComparisons = CompareGates(
                "baseline",
                original.BaselineVerificationResults,
                baselineResults,
                new HashSet<string>(["Build", "Tests"], StringComparer.OrdinalIgnoreCase));
            return new ReplayRun
            {
                Outcome = ExecutionReplayOutcome.CandidateDivergence,
                Message = $"Authenticated diff could not be applied to its baseline: " +
                    FormatFailure(patchApplied),
                Tools = CompareTools(original.BaselineCommands, baselineCommands),
                Commands = CompareCommands(
                    "baseline",
                    NonProbeCommands(original.BaselineCommands),
                    NonProbeCommands(baselineCommands)).ToList(),
                Gates = baselineComparisons.ToList()
            };
        }

        var candidate = await _workspaceManager.CreateCandidateAsync(
            workspace,
            original.TaskContract.Id,
            original.AgentRun.Id.ToString("N"),
            cancellationToken);
        var candidateMatches = CandidateEquals(original.CandidateChangeSet, candidate);

        var candidateCommands = new List<ExecutionCommandEvidence>();
        var candidateContext = CreateContext(
            original,
            workspace.Path,
            candidate,
            candidateCommands);
        var acceptanceCriteria = new List<AcceptanceCriterionResult>();
        var candidateResults = await VerifyCandidateAsync(
            stagedProcessRunner,
            candidateContext,
            acceptanceCriteria,
            cancellationToken);

        var tools = CompareTools(original.BaselineCommands, baselineCommands);
        var commands = CompareCommands(
                "baseline",
                NonProbeCommands(original.BaselineCommands),
                NonProbeCommands(baselineCommands))
            .Concat(CompareCommands(
                "candidate",
                original.CandidateCommands,
                candidateCommands))
            .ToList();
        var gates = CompareGates(
                "baseline",
                original.BaselineVerificationResults,
                baselineResults,
                new HashSet<string>(["Build", "Tests"], StringComparer.OrdinalIgnoreCase))
            .Concat(CompareGates(
                "candidate",
                original.VerificationResults,
                candidateResults,
                SupportedCandidateGates,
                original.FinalDecision.RequiredVerifiers))
            .ToList();
        var acceptanceMatches = AcceptanceEquals(
            original.AcceptanceCriteriaResults,
            acceptanceCriteria);

        ExecutionReplayOutcome outcome;
        string message;
        if (!candidateMatches)
        {
            outcome = ExecutionReplayOutcome.CandidateDivergence;
            message = "Reconstructed candidate differs from the authenticated candidate hash or file set.";
        }
        else if (tools.Any(item => item.Status == ReplayComparisonStatus.Missing) ||
                 gates.Any(item => item.Status is ReplayComparisonStatus.NotReproducible or
                     ReplayComparisonStatus.Missing))
        {
            outcome = ExecutionReplayOutcome.GateNotReproducible;
            message = "One or more original tools or gates cannot be reproduced by this runtime.";
        }
        else if (tools.Any(item => item.Status == ReplayComparisonStatus.Diverged) ||
                 commands.Any(item => item.Definition != ReplayComparisonStatus.Match ||
                     item.Result != ReplayComparisonStatus.Match) ||
                 gates.Any(item => item.Status != ReplayComparisonStatus.Match) ||
                 !acceptanceMatches)
        {
            outcome = ExecutionReplayOutcome.EnvironmentDivergence;
            message = "The candidate is identical, but tools, command results, gates, or acceptance artifacts diverged.";
        }
        else
        {
            outcome = ExecutionReplayOutcome.Reproduced;
            message = "Candidate, tool versions, commands, gates, and acceptance artifacts were reproduced.";
        }

        return new ReplayRun
        {
            Outcome = outcome,
            Message = message,
            ActualDiffHash = candidate.DiffHash,
            Tools = tools,
            Commands = commands,
            Gates = gates,
            AcceptanceCriteria = acceptanceCriteria
        };
    }

    private static VerificationContext CreateContext(
        ExecutionEvidence original,
        string workspacePath,
        CandidateChangeSet candidate,
        List<ExecutionCommandEvidence> commands) => new()
        {
            TaskId = original.TaskContract.Id,
            AgentRunId = original.AgentRun.Id.ToString("N"),
            RepoPath = workspacePath,
            Contract = original.TaskContract,
            AgentResult = original.AgentResult,
            CandidateChangeSet = candidate,
            CommandEvidence = commands
        };

    private static RepositoryExecutionProfile ReplayExecutionProfile(
        ExecutionEvidence original)
    {
        var profile = original.TaskContract.Execution;
        if (profile.Runtime is not null ||
            original.BaselineCommands.Concat(original.CandidateCommands)
                .Any(command => command.Environment is not null))
        {
            return profile;
        }

        // Evidence emitted before sandbox metadata existed necessarily ran staged
        // commands on the host. The CLI still requires its explicit development opt-in.
        return new RepositoryExecutionProfile
        {
            WorkingDirectory = profile.WorkingDirectory,
            Target = profile.Target,
            Runtime = RepositoryExecutionProfile.HostRuntime
        };
    }

    private async Task<List<VerificationResult>> VerifyBaselineAsync(
        IProcessRunner stagedProcessRunner,
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        var results = new List<VerificationResult>();
        if (context.Contract.Verification.Build)
        {
            results.Add(await RunVerifierAsync(
                new BuildVerifier(stagedProcessRunner),
                context,
                cancellationToken));
        }

        var buildPassed = results
            .Where(item => item.Verifier == "Build")
            .All(item => item.Status == VerificationStatus.Pass);
        if (context.Contract.Verification.UnitTests ||
            context.Contract.Verification.IntegrationTests)
        {
            results.Add(buildPassed
                ? await RunVerifierAsync(
                    new TestVerifier(stagedProcessRunner),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Tests", "Baseline build prerequisite failed"));
        }

        return results;
    }

    private async Task<List<VerificationResult>> VerifyCandidateAsync(
        IProcessRunner stagedProcessRunner,
        VerificationContext context,
        List<AcceptanceCriterionResult> acceptanceCriteria,
        CancellationToken cancellationToken)
    {
        var results = new List<VerificationResult>();
        IVerifier[] prerequisites =
        [
            new AgentSuccessVerifier(),
            new FileApplicationVerifier(new FileApplicatorResult { Success = true }),
            new CandidateChangeVerifier(),
            new ScopeVerifier(),
            new BudgetVerifier()
        ];
        foreach (var verifier in prerequisites)
            results.Add(await RunVerifierAsync(verifier, context, cancellationToken));

        var prerequisitesPassed = results.All(item => item.Status == VerificationStatus.Pass);
        if (context.Contract.Verification.Build)
        {
            results.Add(prerequisitesPassed
                ? await RunVerifierAsync(
                    new BuildVerifier(stagedProcessRunner),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Build", "Trust-boundary prerequisite failed"));
        }

        var buildPassed = results
            .Where(item => item.Verifier == "Build")
            .All(item => item.Status == VerificationStatus.Pass);
        if (context.Contract.Verification.UnitTests ||
            context.Contract.Verification.IntegrationTests)
        {
            results.Add(prerequisitesPassed && buildPassed
                ? await RunVerifierAsync(
                    new TestVerifier(stagedProcessRunner),
                    context,
                    cancellationToken)
                : Skipped(context.AgentRunId, "Tests", "Trust-boundary or build prerequisite failed"));
        }

        if (prerequisitesPassed && buildPassed)
        {
            IVerifier[] semanticVerifiers =
            [
                new EB001Verifier(), new EB002Verifier(), new EB003Verifier(),
                new EB004Verifier(), new EB005Verifier()
            ];
            foreach (var verifier in semanticVerifiers)
                results.Add(await RunVerifierAsync(verifier, context, cancellationToken));
        }

        if (AcceptanceCriteriaVerifier.GetEffectiveCriteria(context.Contract).Count > 0)
        {
            var acceptance = await new AcceptanceCriteriaVerifier(stagedProcessRunner).VerifyAsync(
                context,
                results,
                prerequisitesPassed && buildPassed,
                cancellationToken);
            acceptanceCriteria.AddRange(acceptance.Criteria);
            results.Add(acceptance.AggregateResult);
        }

        return results;
    }

    private static async Task<VerificationResult> RunVerifierAsync(
        IVerifier verifier,
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await verifier.VerifyAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = verifier.Name,
                Status = VerificationStatus.Error,
                Severity = Severity.Critical,
                Message = $"Verifier threw an exception: {ex.Message}"
            };
        }
    }

    private async Task<ProcessExecutionResult> ApplyAuthenticatedDiffAsync(
        string workspacePath,
        string diff,
        CancellationToken cancellationToken)
    {
        var patchPath = Path.Combine(
            Path.GetTempPath(),
            $"aecs-replay-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllTextAsync(patchPath, diff, cancellationToken);
            return await _processRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "git",
                Arguments = ["apply", "--index", "--binary", "--whitespace=nowarn", "--", patchPath],
                WorkingDirectory = workspacePath,
                Timeout = TimeSpan.FromMinutes(2)
            }, cancellationToken);
        }
        finally
        {
            if (File.Exists(patchPath))
                File.Delete(patchPath);
        }
    }

    private static List<ReplayToolComparison> CompareTools(
        IReadOnlyList<ExecutionCommandEvidence> expectedCommands,
        IReadOnlyList<ExecutionCommandEvidence> actualCommands)
    {
        var expected = expectedCommands.Where(ToolVersionProbe.IsProbe).ToList();
        var actual = actualCommands.Where(ToolVersionProbe.IsProbe).ToList();
        var tools = new[] { "git", "dotnet" };
        return tools.Select(tool =>
        {
            var expectedMatches = expected.Where(item =>
                string.Equals(item.FileName, tool, StringComparison.OrdinalIgnoreCase)).ToList();
            var actualMatches = actual.Where(item =>
                string.Equals(item.FileName, tool, StringComparison.OrdinalIgnoreCase)).ToList();
            var expectedProbe = expectedMatches.Count == 1 ? expectedMatches[0] : null;
            var actualProbe = actualMatches.Count == 1 ? actualMatches[0] : null;
            var expectedVersion = VersionOutput(expectedProbe);
            var actualVersion = VersionOutput(actualProbe);
            var status = !SuccessfulProbe(expectedProbe) || !SuccessfulProbe(actualProbe)
                ? ReplayComparisonStatus.Missing
                : string.Equals(expectedVersion, actualVersion, StringComparison.Ordinal) &&
                  EnvironmentEquals(expectedProbe!, actualProbe!)
                    ? ReplayComparisonStatus.Match
                    : ReplayComparisonStatus.Diverged;
            return new ReplayToolComparison
            {
                Tool = tool,
                Status = status,
                ExpectedVersion = expectedVersion,
                ActualVersion = actualVersion
            };
        }).ToList();
    }

    private static bool SuccessfulProbe(ExecutionCommandEvidence? command) =>
        command is not null && command.ExitCode == 0 && !command.TimedOut && !command.Cancelled;

    private static IEnumerable<ReplayCommandComparison> CompareCommands(
        string phase,
        IReadOnlyList<ExecutionCommandEvidence> expected,
        IReadOnlyList<ExecutionCommandEvidence> actual)
    {
        var count = Math.Max(expected.Count, actual.Count);
        for (var index = 0; index < count; index++)
        {
            var expectedCommand = index < expected.Count ? expected[index] : null;
            var actualCommand = index < actual.Count ? actual[index] : null;
            var definition = expectedCommand is null || actualCommand is null
                ? ReplayComparisonStatus.Missing
                : CommandDefinitionEquals(expectedCommand, actualCommand)
                    ? ReplayComparisonStatus.Match
                    : ReplayComparisonStatus.Diverged;
            var result = expectedCommand is null || actualCommand is null
                ? ReplayComparisonStatus.Missing
                : CommandResultEquals(expectedCommand, actualCommand)
                    ? ReplayComparisonStatus.Match
                    : ReplayComparisonStatus.Diverged;
            var command = actualCommand ?? expectedCommand!;
            yield return new ReplayCommandComparison
            {
                Phase = phase,
                FileName = command.FileName,
                Arguments = command.Arguments,
                Definition = definition,
                Result = result,
                ExpectedExitCode = expectedCommand?.ExitCode,
                ActualExitCode = actualCommand?.ExitCode,
                Message = definition == ReplayComparisonStatus.Match &&
                    result == ReplayComparisonStatus.Match
                    ? "Command definition and result matched."
                    : "Command definition or result diverged."
            };
        }
    }

    private static IEnumerable<ReplayGateComparison> CompareGates(
        string phase,
        IReadOnlyList<VerificationResult> expected,
        IReadOnlyList<VerificationResult> actual,
        IReadOnlySet<string> supported,
        IEnumerable<string>? required = null)
    {
        var names = expected.Select(item => item.Verifier)
            .Concat(actual.Select(item => item.Verifier))
            .Concat(required ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var expectedMatches = expected.Where(item => string.Equals(
                item.Verifier,
                name,
                StringComparison.OrdinalIgnoreCase)).ToList();
            var actualMatches = actual.Where(item => string.Equals(
                item.Verifier,
                name,
                StringComparison.OrdinalIgnoreCase)).ToList();
            ReplayComparisonStatus status;
            if (!supported.Contains(name))
                status = ReplayComparisonStatus.NotReproducible;
            else if (expectedMatches.Count != 1 || actualMatches.Count != 1)
                status = ReplayComparisonStatus.Missing;
            else
                status = expectedMatches[0].Status == actualMatches[0].Status
                    ? ReplayComparisonStatus.Match
                    : ReplayComparisonStatus.Diverged;

            yield return new ReplayGateComparison
            {
                Gate = $"{phase}:{name}",
                Status = status,
                Expected = expectedMatches.Count == 1
                    ? expectedMatches[0].Status.ToString()
                    : "missing-or-ambiguous",
                Actual = actualMatches.Count == 1
                    ? actualMatches[0].Status.ToString()
                    : "missing-or-ambiguous",
                Message = status switch
                {
                    ReplayComparisonStatus.Match => "Gate status matched.",
                    ReplayComparisonStatus.NotReproducible => "Gate is not implemented by the replay runtime.",
                    ReplayComparisonStatus.Missing => "Gate result is missing or ambiguous.",
                    _ => "Gate status diverged."
                }
            };
        }
    }

    private static bool AcceptanceEquals(
        IReadOnlyList<AcceptanceCriterionResult> expected,
        IReadOnlyList<AcceptanceCriterionResult> actual)
    {
        if (expected.Count != actual.Count)
            return false;
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            if (!string.Equals(left.CriterionId, right.CriterionId, StringComparison.Ordinal) ||
                left.Status != right.Status ||
                left.EvidenceType != right.EvidenceType ||
                !string.Equals(left.EvidenceReference, right.EvidenceReference, StringComparison.Ordinal) ||
                !string.Equals(left.TestPath, right.TestPath, StringComparison.OrdinalIgnoreCase) ||
                !NormalizeReferences(left.EvidenceReferences).SequenceEqual(
                    NormalizeReferences(right.EvidenceReferences),
                    StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> NormalizeReferences(IEnumerable<string> references) =>
        references.Select(reference =>
            reference.StartsWith("execution-command:", StringComparison.OrdinalIgnoreCase)
                ? "execution-command:*"
                : reference.StartsWith("verification-result:", StringComparison.OrdinalIgnoreCase)
                    ? "verification-result:*"
                    : reference);

    private static bool CandidateEquals(CandidateChangeSet expected, CandidateChangeSet actual) =>
        string.Equals(expected.DiffHash, actual.DiffHash, StringComparison.Ordinal) &&
        expected.ChangedFiles.SequenceEqual(actual.ChangedFiles, StringComparer.OrdinalIgnoreCase);

    private static bool CommandDefinitionEquals(
        ExecutionCommandEvidence expected,
        ExecutionCommandEvidence actual) =>
        string.Equals(expected.FileName, actual.FileName, StringComparison.OrdinalIgnoreCase) &&
        expected.Arguments.SequenceEqual(actual.Arguments, StringComparer.Ordinal) &&
        string.Equals(
            NormalizeWorkingDirectory(expected.WorkingDirectory),
            NormalizeWorkingDirectory(actual.WorkingDirectory),
            StringComparison.OrdinalIgnoreCase) &&
        EnvironmentEquals(expected, actual);

    private static bool EnvironmentEquals(
        ExecutionCommandEvidence expected,
        ExecutionCommandEvidence actual)
    {
        // Legacy authenticated commands did not carry environment metadata.
        if (expected.Environment is null)
            return true;
        var left = expected.Environment;
        var right = actual.Environment;
        return right is not null &&
            left.Runtime == right.Runtime &&
            left.RuntimeVersion == right.RuntimeVersion &&
            left.Image == right.Image &&
            left.ImageDigest == right.ImageDigest &&
            left.NetworkMode == right.NetworkMode &&
            left.CpuLimit == right.CpuLimit &&
            left.MemoryLimit == right.MemoryLimit &&
            left.ProcessLimit == right.ProcessLimit &&
            left.WorkspaceMount == right.WorkspaceMount &&
            left.DevelopmentHostOverride == right.DevelopmentHostOverride;
    }

    private static bool CommandResultEquals(
        ExecutionCommandEvidence expected,
        ExecutionCommandEvidence actual) =>
        expected.ExitCode == actual.ExitCode &&
        expected.TimedOut == actual.TimedOut &&
        expected.Cancelled == actual.Cancelled;

    private static IReadOnlyList<ExecutionCommandEvidence> NonProbeCommands(
        IReadOnlyList<ExecutionCommandEvidence> commands) =>
        commands.Where(command => !ToolVersionProbe.IsProbe(command)).ToList();

    private static string VersionOutput(ExecutionCommandEvidence? command) => command is null
        ? string.Empty
        : string.Join('\n', new[] { command.StandardOutput, command.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string NormalizeWorkingDirectory(string path) =>
        string.IsNullOrWhiteSpace(path) ? "." : path.Replace('\\', '/').TrimEnd('/');

    private static VerificationResult Skipped(
        string agentRunId,
        string verifier,
        string reason) => new()
        {
            AgentRunId = agentRunId,
            Verifier = verifier,
            Status = VerificationStatus.Skip,
            Severity = Severity.Warning,
            Message = reason
        };

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsGitObjectId(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static string FormatFailure(ProcessExecutionResult result)
    {
        if (result.TimedOut)
            return "command timed out";
        if (result.Cancelled)
            return "command was cancelled";
        return $"exit code {result.ExitCode}: {result.StandardError.Trim()}";
    }

    private sealed class ReplayRun
    {
        public ExecutionReplayOutcome Outcome { get; init; }
        public string Message { get; init; } = string.Empty;
        public string ActualDiffHash { get; init; } = string.Empty;
        public List<ReplayToolComparison> Tools { get; init; } = [];
        public List<ReplayCommandComparison> Commands { get; init; } = [];
        public List<ReplayGateComparison> Gates { get; init; } = [];
        public List<AcceptanceCriterionResult> AcceptanceCriteria { get; init; } = [];

        public static ReplayRun Failed(ExecutionReplayOutcome outcome, string message) => new()
        {
            Outcome = outcome,
            Message = message
        };
    }
}
