using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.Experiments;

public sealed class ExperimentRunner
{
    private readonly StagedExecutionPipeline? _legacyPipeline;
    private readonly Func<ExperimentRunDefinition, CancellationToken, Task<StagedExecutionResult>>
        _execute;
    private readonly TaskContractParser _parser = new();

    public ExperimentRunner(StagedExecutionPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _legacyPipeline = pipeline;
        _execute = (definition, cancellationToken) => pipeline.RunAsync(
            definition.RepositoryPath,
            _parser.ParseFromFile(definition.ContractPath),
            cancellationToken);
    }

    public ExperimentRunner(
        Func<ExperimentRunDefinition, CancellationToken, Task<StagedExecutionResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
    }

    public async Task<ExperimentReport> RunAsync(
        string repoPath,
        IEnumerable<string> taskFiles,
        CancellationToken cancellationToken)
    {
        if (_legacyPipeline is null)
        {
            throw new InvalidOperationException(
                "Legacy task-directory experiments require a fixed pipeline.");
        }

        var results = new List<TaskExperimentResult>();
        foreach (var taskFile in taskFiles)
        {
            var contract = _parser.ParseFromFile(taskFile);
            var execution = await _legacyPipeline.RunAsync(
                repoPath,
                contract,
                cancellationToken);
            results.Add(MapLegacy(execution));
        }
        return new ExperimentReport { Results = results };
    }

    public async Task<ExperimentReport> RunDatasetAsync(
        LoadedExperimentDataset dataset,
        string baselineCommit,
        ExperimentArtifactStore artifacts,
        bool resume,
        bool includeRealProviders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineCommit);
        ArgumentNullException.ThrowIfNull(artifacts);
        ExperimentDatasetContract.Validate(dataset.Manifest);
        if (!ExperimentDatasetContract.BaselineMatches(
                dataset.Manifest.Repository.Baseline,
                baselineCommit))
        {
            throw new InvalidOperationException(
                $"Dataset baseline '{dataset.Manifest.Repository.Baseline}' does not match " +
                $"repository HEAD '{baselineCommit}'.");
        }
        ExperimentArtifactStore.EnsureOutsideRepository(
            artifacts.OutputDirectory,
            dataset.RepositoryPath);

        var contracts = dataset.Manifest.Tasks.ToDictionary(
            task => task.Id,
            task => _parser.ParseFromFile(dataset.ContractPath(task)),
            StringComparer.Ordinal);
        foreach (var task in dataset.Manifest.Tasks)
        {
            if (!contracts[task.Id].Id.Equals(task.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dataset task '{task.Id}' does not match contract ID " +
                    $"'{contracts[task.Id].Id}'.");
            }
        }

        var datasetHash = ExperimentDatasetFingerprint.Create(
            dataset.Manifest,
            baselineCommit);
        var session = await artifacts.InitializeAsync(
            datasetHash,
            resume,
            cancellationToken);
        var results = new List<TaskExperimentResult>();
        var environment = ExperimentEnvironment.Capture();

        foreach (var task in dataset.Manifest.Tasks.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (var repetition in Enumerable.Range(1, dataset.Manifest.Repetitions))
            {
                foreach (var variant in dataset.Manifest.Variants.OrderBy(
                             item => item.Id,
                             StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var definition = Definition(
                        dataset,
                        datasetHash,
                        baselineCommit,
                        task,
                        variant,
                        repetition);
                    var checkpoint = resume
                        ? await artifacts.LoadAsync(
                            definition.RunKey,
                            datasetHash,
                            cancellationToken)
                        : null;
                    if (checkpoint is not null)
                    {
                        results.Add(checkpoint);
                        continue;
                    }

                    TaskExperimentResult result;
                    if (variant.RequiresRealProvider && !includeRealProviders)
                    {
                        result = Failure(
                            definition,
                            contracts[task.Id],
                            ExperimentResultStatus.Skipped,
                            "Variant requires a real provider; pass --include-real-providers.");
                    }
                    else
                    {
                        result = await ExecuteAsync(
                            definition,
                            contracts[task.Id],
                            cancellationToken);
                    }

                    await artifacts.SaveAsync(result, cancellationToken);
                    results.Add(result);
                    await artifacts.SaveReportAsync(
                        BuildReport(dataset, datasetHash, baselineCommit, session, environment, results),
                        cancellationToken);
                }
            }
        }

        var report = BuildReport(
            dataset,
            datasetHash,
            baselineCommit,
            session,
            environment,
            results);
        await artifacts.SaveReportAsync(report, cancellationToken);
        return report;
    }

    private async Task<TaskExperimentResult> ExecuteAsync(
        ExperimentRunDefinition definition,
        TaskContract contract,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = await _execute(definition, cancellationToken);
            var invariantFailures = new List<string>();
            if (!execution.Contract.Id.Equals(definition.Task.Id, StringComparison.Ordinal))
                invariantFailures.Add("execution returned a different task contract");
            if (!execution.Baseline.Commit.Equals(
                    definition.BaselineCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                invariantFailures.Add("execution used a different baseline");
            }
            if (!execution.OriginalRepositoryUnchanged)
                invariantFailures.Add("original repository changed during the run");
            if (execution.EvidenceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(execution.EvidenceLocation))
            {
                invariantFailures.Add("execution did not persist an origin evidence link");
            }

            return Map(
                definition,
                execution,
                invariantFailures.Count == 0
                    ? ExperimentResultStatus.Completed
                    : ExperimentResultStatus.Failed,
                string.Join("; ", invariantFailures));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(
                definition,
                contract,
                ExperimentResultStatus.Failed,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ExperimentRunDefinition Definition(
        LoadedExperimentDataset dataset,
        string datasetHash,
        string baselineCommit,
        ExperimentTaskDefinition task,
        ExperimentVariantDefinition variant,
        int repetition)
    {
        int? effectiveSeed = null;
        if (variant.Seed is { } seed)
            effectiveSeed = checked(seed + repetition - 1);
        return new ExperimentRunDefinition
        {
            DatasetId = dataset.Manifest.Id,
            DatasetVersion = dataset.Manifest.Version,
            DatasetHash = datasetHash,
            RunKey = ExperimentDatasetFingerprint.RunKey(
                datasetHash,
                task.Id,
                variant.Id,
                repetition),
            RepositoryPath = dataset.RepositoryPath,
            BaselineCommit = baselineCommit,
            Task = task,
            ContractPath = dataset.ContractPath(task),
            Variant = variant,
            Repetition = repetition,
            EffectiveSeed = effectiveSeed
        };
    }

    private static TaskExperimentResult Map(
        ExperimentRunDefinition definition,
        StagedExecutionResult execution,
        ExperimentResultStatus status,
        string failure) => new()
        {
            RunKey = definition.RunKey,
            Status = status,
            DatasetId = definition.DatasetId,
            DatasetVersion = definition.DatasetVersion,
            DatasetHash = definition.DatasetHash,
            VariantId = definition.Variant.Id,
            Repetition = definition.Repetition,
            Provider = definition.Variant.Provider.ToString(),
            Adapter = execution.ContextManifest.Adapter,
            RequestedModel = definition.Variant.Model,
            ContextStrategy = definition.Variant.ContextStrategy,
            ContextConfiguration = definition.Variant.Context,
            ActualContextStrategy = execution.ContextManifest.Strategy,
            ContextStrategyVersion = execution.ContextManifest.StrategyVersion,
            ContextManifestHash = execution.ContextManifest.ManifestHash,
            Seed = definition.EffectiveSeed,
            Parameters = new Dictionary<string, string>(
                definition.Variant.Parameters,
                StringComparer.Ordinal),
            BaselineCommit = execution.Baseline.Commit,
            ExpectedDecision = definition.Task.ExpectedDecision,
            MatchesExpected = status == ExperimentResultStatus.Completed &&
                execution.Decision.Decision == definition.Task.ExpectedDecision,
            Failure = failure,
            TaskId = execution.Contract.Id,
            Objective = execution.Contract.Objective,
            Risk = execution.Risk,
            Model = execution.Model,
            Decision = execution.Decision.Decision,
            DecisionReason = execution.Decision.Reason,
            Duration = execution.BudgetUsage.WallClockElapsed,
            InputTokens = execution.AgentResult.InputTokens,
            OutputTokens = execution.AgentResult.OutputTokens,
            EstimatedCost = execution.AgentResult.EstimatedCost,
            FilesChanged = execution.CandidateChangeSet.ChangedFiles.Count,
            Verifications = execution.VerificationResults.ToDictionary(
                result => result.Verifier,
                result => result.Status),
            AcceptanceCriteria = execution.AcceptanceCriteriaResults.ToList(),
            AgentAttempts = execution.AgentAttempts.ToList(),
            BudgetUsage = execution.BudgetUsage,
            RetryCount = execution.AgentRun.RetryCount,
            VerifiedCodeChange = status == ExperimentResultStatus.Completed &&
                execution.Decision.Decision == TaskDecision.Verified &&
                execution.OriginalRepositoryUnchanged,
            FirstPassVerified = status == ExperimentResultStatus.Completed &&
                execution.Decision.Decision == TaskDecision.Verified &&
                execution.OriginalRepositoryUnchanged &&
                execution.AgentRun.RetryCount == 0,
            ScopeViolationCount = execution.VerificationResults.Count(result =>
                result.Verifier.Equals("Scope", StringComparison.OrdinalIgnoreCase) &&
                result.Status == VerificationStatus.Fail),
            ReworkCount = execution.AgentRun.RetryCount,
            EvidenceId = execution.EvidenceId,
            EvidenceLocation = execution.EvidenceLocation,
            OriginalRepositoryUnchanged = execution.OriginalRepositoryUnchanged
        };

    private static TaskExperimentResult Failure(
        ExperimentRunDefinition definition,
        TaskContract contract,
        ExperimentResultStatus status,
        string failure) => new()
        {
            RunKey = definition.RunKey,
            Status = status,
            DatasetId = definition.DatasetId,
            DatasetVersion = definition.DatasetVersion,
            DatasetHash = definition.DatasetHash,
            VariantId = definition.Variant.Id,
            Repetition = definition.Repetition,
            Provider = definition.Variant.Provider.ToString(),
            RequestedModel = definition.Variant.Model,
            ContextStrategy = definition.Variant.ContextStrategy,
            ContextConfiguration = definition.Variant.Context,
            Seed = definition.EffectiveSeed,
            Parameters = new Dictionary<string, string>(
                definition.Variant.Parameters,
                StringComparer.Ordinal),
            BaselineCommit = definition.BaselineCommit,
            ExpectedDecision = definition.Task.ExpectedDecision,
            MatchesExpected = false,
            Failure = failure,
            TaskId = contract.Id,
            Objective = contract.Objective,
            Risk = contract.Constraints.SecurityRisk,
            Model = definition.Variant.Model,
            DecisionReason = failure,
            OriginalRepositoryUnchanged = true
        };

    private static TaskExperimentResult MapLegacy(StagedExecutionResult execution) => new()
        {
            TaskId = execution.Contract.Id,
            Objective = execution.Contract.Objective,
            Risk = execution.Risk,
            Model = execution.Model,
            Decision = execution.Decision.Decision,
            DecisionReason = execution.Decision.Reason,
            Duration = execution.BudgetUsage.WallClockElapsed,
            InputTokens = execution.AgentResult.InputTokens,
            OutputTokens = execution.AgentResult.OutputTokens,
            EstimatedCost = execution.AgentResult.EstimatedCost,
            FilesChanged = execution.CandidateChangeSet.ChangedFiles.Count,
            Verifications = execution.VerificationResults.ToDictionary(
                result => result.Verifier,
                result => result.Status),
            AcceptanceCriteria = execution.AcceptanceCriteriaResults.ToList(),
            AgentAttempts = execution.AgentAttempts.ToList(),
            BudgetUsage = execution.BudgetUsage,
            RetryCount = execution.AgentRun.RetryCount,
            VerifiedCodeChange = execution.Decision.Decision == TaskDecision.Verified &&
                execution.OriginalRepositoryUnchanged,
            FirstPassVerified = execution.Decision.Decision == TaskDecision.Verified &&
                execution.OriginalRepositoryUnchanged &&
                execution.AgentRun.RetryCount == 0,
            ScopeViolationCount = execution.VerificationResults.Count(result =>
                result.Verifier.Equals("Scope", StringComparison.OrdinalIgnoreCase) &&
                result.Status == VerificationStatus.Fail),
            ReworkCount = execution.AgentRun.RetryCount,
            EvidenceId = execution.EvidenceId,
            EvidenceLocation = execution.EvidenceLocation,
            OriginalRepositoryUnchanged = execution.OriginalRepositoryUnchanged
        };

    private static ExperimentReport BuildReport(
        LoadedExperimentDataset dataset,
        string datasetHash,
        string baselineCommit,
        ExperimentSession session,
        ExperimentEnvironment environment,
        IEnumerable<TaskExperimentResult> results)
    {
        var ordered = results.OrderBy(result => result.TaskId, StringComparer.Ordinal)
            .ThenBy(result => result.Repetition)
            .ThenBy(result => result.VariantId, StringComparer.Ordinal)
            .ToList();
        var comparisons = Compare(ordered, dataset.Manifest.ReferenceVariantId);
        return new ExperimentReport
        {
            ExperimentId = ExperimentDatasetFingerprint.ExperimentId(datasetHash),
            ExecutedAt = session.StartedAt,
            DatasetId = dataset.Manifest.Id,
            DatasetVersion = dataset.Manifest.Version,
            DatasetHash = datasetHash,
            ManifestPath = dataset.ManifestPath,
            RepositoryPath = dataset.RepositoryPath,
            BaselineCommit = baselineCommit,
            ReferenceVariantId = dataset.Manifest.ReferenceVariantId,
            Repetitions = dataset.Manifest.Repetitions,
            Environment = environment,
            Results = ordered,
            PairedComparisons = comparisons,
            Analysis = ExperimentAnalyzer.Analyze(dataset.Manifest, ordered, comparisons)
        };
    }

    private static List<ExperimentPairedComparison> Compare(
        IReadOnlyCollection<TaskExperimentResult> results,
        string referenceVariantId)
    {
        var comparisons = new List<ExperimentPairedComparison>();
        foreach (var group in results.GroupBy(result =>
                     $"{result.TaskId}\n{result.Repetition}",
                     StringComparer.Ordinal))
        {
            var reference = group.SingleOrDefault(result => result.VariantId.Equals(
                referenceVariantId,
                StringComparison.Ordinal));
            if (reference is null)
                continue;
            foreach (var candidate in group.Where(result => !result.VariantId.Equals(
                         referenceVariantId,
                         StringComparison.Ordinal)))
            {
                var bothCompleted = reference.Status == ExperimentResultStatus.Completed &&
                    candidate.Status == ExperimentResultStatus.Completed;
                comparisons.Add(new ExperimentPairedComparison
                {
                    TaskId = reference.TaskId,
                    Repetition = reference.Repetition,
                    ReferenceVariantId = reference.VariantId,
                    CandidateVariantId = candidate.VariantId,
                    ReferenceRunKey = reference.RunKey,
                    CandidateRunKey = candidate.RunKey,
                    ReferenceStatus = reference.Status,
                    CandidateStatus = candidate.Status,
                    BothCompleted = bothCompleted,
                    DecisionChanged = bothCompleted && reference.Decision != candidate.Decision,
                    ReferenceVerifiedCodeChange = reference.VerifiedCodeChange,
                    CandidateVerifiedCodeChange = candidate.VerifiedCodeChange,
                    VerifiedCodeChangeDelta = Bool(candidate.VerifiedCodeChange) -
                        Bool(reference.VerifiedCodeChange),
                    ReferenceFirstPass = reference.FirstPassVerified,
                    CandidateFirstPass = candidate.FirstPassVerified,
                    FirstPassDelta = Bool(candidate.FirstPassVerified) -
                        Bool(reference.FirstPassVerified),
                    ReferenceTotalTokens = reference.TotalTokens,
                    CandidateTotalTokens = candidate.TotalTokens,
                    TotalTokenDelta = candidate.TotalTokens - reference.TotalTokens,
                    ReferenceEstimatedCost = reference.EstimatedCost,
                    CandidateEstimatedCost = candidate.EstimatedCost,
                    DurationDeltaSeconds = candidate.Duration.TotalSeconds -
                        reference.Duration.TotalSeconds,
                    CostDelta = candidate.EstimatedCost - reference.EstimatedCost,
                    ScopeViolationDelta = candidate.ScopeViolationCount -
                        reference.ScopeViolationCount,
                    ReworkDelta = candidate.ReworkCount - reference.ReworkCount,
                    ReferenceEvidenceId = reference.EvidenceId == Guid.Empty
                        ? null
                        : reference.EvidenceId,
                    CandidateEvidenceId = candidate.EvidenceId == Guid.Empty
                        ? null
                        : candidate.EvidenceId,
                    Failure = bothCompleted
                        ? string.Empty
                        : $"reference={reference.Status}:{reference.Failure}; " +
                          $"candidate={candidate.Status}:{candidate.Failure}"
                });
            }
        }
        return comparisons.OrderBy(pair => pair.TaskId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Repetition)
            .ThenBy(pair => pair.CandidateVariantId, StringComparer.Ordinal)
            .ToList();
    }

    private static int Bool(bool value) => value ? 1 : 0;
}
