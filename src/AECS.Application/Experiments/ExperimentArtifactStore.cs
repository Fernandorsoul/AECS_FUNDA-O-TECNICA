using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Application.Experiments;

public sealed class ExperimentRunCheckpoint
{
    public string SchemaVersion { get; init; } = ExperimentDatasetSchema.CheckpointVersion;
    public DateTime SavedAt { get; init; } = DateTime.UtcNow;
    public string ContentHash { get; init; } = string.Empty;
    public TaskExperimentResult Result { get; init; } = new();
}

public sealed class ExperimentSession
{
    public string SchemaVersion { get; init; } = ExperimentDatasetSchema.CheckpointVersion;
    public string DatasetHash { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

public sealed class ExperimentArtifactStore
{
    private readonly string _runsDirectory;

    public ExperimentArtifactStore(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = Path.GetFullPath(outputDirectory);
        _runsDirectory = Path.Combine(OutputDirectory, "runs");
    }

    public string OutputDirectory { get; }
    public string ReportPath => Path.Combine(OutputDirectory, "report.json");
    public string ResultsCsvPath => Path.Combine(OutputDirectory, "results.csv");
    public string ComparisonsCsvPath => Path.Combine(OutputDirectory, "comparisons.csv");
    public string AnalysisCsvPath => Path.Combine(OutputDirectory, "analysis.csv");
    public string CostRecordsCsvPath => Path.Combine(OutputDirectory, "cost-records.csv");
    public string CostEfficiencyCsvPath => Path.Combine(OutputDirectory, "cost-efficiency.csv");

    public static void EnsureOutsideRepository(string outputDirectory, string repositoryPath)
    {
        var output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var repository = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar);
        if (output.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(
                repository + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Experiment output must be outside the dataset repository to prevent contamination.");
        }
    }

    public async Task<ExperimentSession> InitializeAsync(
        string datasetHash,
        bool resume,
        CancellationToken cancellationToken)
    {
        ValidateHash(datasetHash);
        var existingEntries = Directory.Exists(OutputDirectory)
            ? Directory.EnumerateFileSystemEntries(OutputDirectory).ToList()
            : [];
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(_runsDirectory);
        var sessionPath = Path.Combine(OutputDirectory, "session.json");
        if (File.Exists(sessionPath))
        {
            var existing = await ReadAsync<ExperimentSession>(sessionPath, cancellationToken);
            if (existing.SchemaVersion != ExperimentDatasetSchema.CheckpointVersion ||
                !existing.DatasetHash.Equals(datasetHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Experiment output belongs to a different dataset or schema.");
            }
            if (!resume)
            {
                throw new InvalidOperationException(
                    "Experiment output already exists; pass --resume to continue idempotently.");
            }
            return existing;
        }
        if (existingEntries.Count > 0)
        {
            throw new InvalidOperationException(
                resume
                    ? "Experiment artifacts exist without a valid session."
                    : "Experiment output is not empty; select a new directory.");
        }

        var session = new ExperimentSession
        {
            DatasetHash = datasetHash,
            StartedAt = DateTime.UtcNow
        };
        await WriteNewAsync(sessionPath, session, cancellationToken);
        return session;
    }

    public async Task<TaskExperimentResult?> LoadAsync(
        string runKey,
        string datasetHash,
        CancellationToken cancellationToken)
    {
        ValidateRunKey(runKey);
        ValidateHash(datasetHash);
        var path = CheckpointPath(runKey);
        if (!File.Exists(path))
            return null;
        var checkpoint = await ReadAsync<ExperimentRunCheckpoint>(path, cancellationToken);
        ValidateCheckpoint(checkpoint, runKey, datasetHash);
        return checkpoint.Result;
    }

    public async Task SaveAsync(
        TaskExperimentResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateRunKey(result.RunKey);
        ValidateHash(result.DatasetHash);
        var checkpoint = new ExperimentRunCheckpoint
        {
            SavedAt = DateTime.UtcNow,
            ContentHash = ExperimentCheckpointFingerprint.Create(result),
            Result = result
        };
        var target = CheckpointPath(result.RunKey);
        if (File.Exists(target))
        {
            var existing = await ReadAsync<ExperimentRunCheckpoint>(target, cancellationToken);
            ValidateCheckpoint(existing, result.RunKey, result.DatasetHash);
            if (!Equivalent(existing.Result, result))
                throw new InvalidOperationException("Experiment run checkpoint is immutable.");
            return;
        }

        await WriteNewAsync(target, checkpoint, cancellationToken);
    }

    public async Task SaveReportAsync(
        ExperimentReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        await WriteReplaceAsync(ReportPath, report, cancellationToken);
        await WriteTextReplaceAsync(
            ResultsCsvPath,
            ExperimentCsvFormatter.Results(report),
            cancellationToken);
        await WriteTextReplaceAsync(
            ComparisonsCsvPath,
            ExperimentCsvFormatter.Comparisons(report),
            cancellationToken);
        await WriteTextReplaceAsync(
            AnalysisCsvPath,
            ExperimentCsvFormatter.Analysis(report),
            cancellationToken);
        await WriteTextReplaceAsync(
            CostRecordsCsvPath,
            ExperimentCsvFormatter.CostRecords(report),
            cancellationToken);
        await WriteTextReplaceAsync(
            CostEfficiencyCsvPath,
            ExperimentCsvFormatter.CostEfficiency(report),
            cancellationToken);
    }

    private string CheckpointPath(string runKey) => Path.Combine(_runsDirectory, runKey + ".json");

    private static void ValidateCheckpoint(
        ExperimentRunCheckpoint checkpoint,
        string runKey,
        string datasetHash)
    {
        if (checkpoint.SchemaVersion != ExperimentDatasetSchema.CheckpointVersion ||
            checkpoint.SavedAt.Kind != DateTimeKind.Utc ||
            checkpoint.Result is null ||
            !checkpoint.ContentHash.Equals(
                ExperimentCheckpointFingerprint.Create(checkpoint.Result),
                StringComparison.Ordinal) ||
            !checkpoint.Result.RunKey.Equals(runKey, StringComparison.Ordinal) ||
            !checkpoint.Result.DatasetHash.Equals(datasetHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Experiment run checkpoint is inconsistent.");
        }
    }

    private static bool Equivalent(TaskExperimentResult left, TaskExperimentResult right) =>
        JsonSerializer.Serialize(left, ExperimentDatasetLoader.SerializerOptions).Equals(
            JsonSerializer.Serialize(right, ExperimentDatasetLoader.SerializerOptions),
            StringComparison.Ordinal);

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            },
            cancellationToken);
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(
                   ExperimentDatasetLoader.SerializerOptions) ??
            throw new InvalidOperationException($"Experiment artifact is empty: {path}");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException("Experiment artifact has duplicate properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static async Task WriteNewAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteJsonAsync(temporary, value, cancellationToken);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task WriteReplaceAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteJsonAsync(temporary, value, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            ExperimentDatasetLoader.SerializerOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteTextReplaceAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidateRunKey(string value)
    {
        if (value.Length != 64 || value.Any(character =>
                !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidOperationException("Experiment run key is invalid.");
        }
    }

    private static void ValidateHash(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value[7..].Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidOperationException("Experiment dataset hash is invalid.");
        }
    }
}

public static class ExperimentCheckpointFingerprint
{
    public static string Create(TaskExperimentResult result)
    {
        var json = JsonSerializer.Serialize(result, ExperimentDatasetLoader.SerializerOptions);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }
}

public static class ExperimentCsvFormatter
{
    public static string Results(ExperimentReport report)
    {
        var rows = new List<string>
        {
            Row(
                "dataset_id", "dataset_version", "dataset_hash", "run_key", "task_id",
                "variant_id", "repetition", "status", "provider", "adapter", "requested_model",
                "actual_model", "context_strategy", "actual_context_strategy",
                "context_strategy_version", "included_context_json", "context_max_tokens",
                "context_max_characters",
                "context_max_file_tokens", "context_max_file_characters",
                "context_dependency_depth", "seed", "parameters_json", "baseline_commit",
                "expected_decision",
                "actual_decision", "matches_expected", "duration_seconds", "input_tokens",
                "output_tokens", "estimated_cost", "files_changed", "retry_count",
                "verified_code_change", "first_pass_verified", "scope_violation_count",
                "rework_count",
                "evidence_id", "evidence_location", "original_repository_unchanged", "failure")
        };
        rows.AddRange(report.Results.Select(result => Row(
            result.DatasetId,
            result.DatasetVersion,
            result.DatasetHash,
            result.RunKey,
            result.TaskId,
            result.VariantId,
            result.Repetition.ToString(CultureInfo.InvariantCulture),
            result.Status.ToString(),
            result.Provider,
            result.Adapter,
            result.RequestedModel,
            result.Model,
            result.ContextStrategy,
            result.ActualContextStrategy,
            result.ContextStrategyVersion,
            JsonSerializer.Serialize(result.IncludedContext),
            result.ContextConfiguration.MaxTokens.ToString(CultureInfo.InvariantCulture),
            result.ContextConfiguration.MaxCharacters.ToString(CultureInfo.InvariantCulture),
            result.ContextConfiguration.MaxFileTokens.ToString(CultureInfo.InvariantCulture),
            result.ContextConfiguration.MaxFileCharacters.ToString(CultureInfo.InvariantCulture),
            result.ContextConfiguration.DependencyDepth.ToString(CultureInfo.InvariantCulture),
            result.Seed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            JsonSerializer.Serialize(
                result.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
                    .ToDictionary(parameter => parameter.Key, parameter => parameter.Value)),
            result.BaselineCommit,
            result.ExpectedDecision?.ToString() ?? string.Empty,
            result.Decision?.ToString() ?? string.Empty,
            result.MatchesExpected.ToString(CultureInfo.InvariantCulture),
            result.Duration.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture),
            result.InputTokens.ToString(CultureInfo.InvariantCulture),
            result.OutputTokens.ToString(CultureInfo.InvariantCulture),
            result.EstimatedCost.ToString(CultureInfo.InvariantCulture),
            result.FilesChanged.ToString(CultureInfo.InvariantCulture),
            result.RetryCount.ToString(CultureInfo.InvariantCulture),
            result.VerifiedCodeChange.ToString(CultureInfo.InvariantCulture),
            result.FirstPassVerified.ToString(CultureInfo.InvariantCulture),
            result.ScopeViolationCount.ToString(CultureInfo.InvariantCulture),
            result.ReworkCount.ToString(CultureInfo.InvariantCulture),
            result.EvidenceId == Guid.Empty ? string.Empty : result.EvidenceId.ToString("N"),
            result.EvidenceLocation,
            result.OriginalRepositoryUnchanged.ToString(CultureInfo.InvariantCulture),
            result.Failure)));
        return string.Join("\n", rows) + "\n";
    }

    public static string Comparisons(ExperimentReport report)
    {
        var rows = new List<string>
        {
            Row(
                "task_id", "repetition", "reference_variant_id", "candidate_variant_id",
                "reference_run_key", "candidate_run_key", "reference_status",
                "candidate_status", "reference_context_json", "candidate_context_json",
                "effective_context_changed", "both_completed", "decision_changed",
                "reference_vcc", "candidate_vcc", "vcc_delta", "reference_first_pass",
                "candidate_first_pass", "first_pass_delta", "reference_total_tokens",
                "candidate_total_tokens", "total_token_delta", "reference_estimated_cost",
                "candidate_estimated_cost", "duration_delta_seconds", "cost_delta",
                "scope_violation_delta", "rework_delta", "reference_evidence_id",
                "candidate_evidence_id", "failure")
        };
        rows.AddRange(report.PairedComparisons.Select(pair => Row(
            pair.TaskId,
            pair.Repetition.ToString(CultureInfo.InvariantCulture),
            pair.ReferenceVariantId,
            pair.CandidateVariantId,
            pair.ReferenceRunKey,
            pair.CandidateRunKey,
            pair.ReferenceStatus.ToString(),
            pair.CandidateStatus.ToString(),
            JsonSerializer.Serialize(pair.ReferenceIncludedContext),
            JsonSerializer.Serialize(pair.CandidateIncludedContext),
            pair.EffectiveContextChanged.ToString(CultureInfo.InvariantCulture),
            pair.BothCompleted.ToString(CultureInfo.InvariantCulture),
            pair.DecisionChanged.ToString(CultureInfo.InvariantCulture),
            pair.ReferenceVerifiedCodeChange.ToString(CultureInfo.InvariantCulture),
            pair.CandidateVerifiedCodeChange.ToString(CultureInfo.InvariantCulture),
            pair.VerifiedCodeChangeDelta.ToString(CultureInfo.InvariantCulture),
            pair.ReferenceFirstPass.ToString(CultureInfo.InvariantCulture),
            pair.CandidateFirstPass.ToString(CultureInfo.InvariantCulture),
            pair.FirstPassDelta.ToString(CultureInfo.InvariantCulture),
            pair.ReferenceTotalTokens.ToString(CultureInfo.InvariantCulture),
            pair.CandidateTotalTokens.ToString(CultureInfo.InvariantCulture),
            pair.TotalTokenDelta.ToString(CultureInfo.InvariantCulture),
            pair.ReferenceEstimatedCost.ToString(CultureInfo.InvariantCulture),
            pair.CandidateEstimatedCost.ToString(CultureInfo.InvariantCulture),
            pair.DurationDeltaSeconds.ToString("F6", CultureInfo.InvariantCulture),
            pair.CostDelta.ToString(CultureInfo.InvariantCulture),
            pair.ScopeViolationDelta.ToString(CultureInfo.InvariantCulture),
            pair.ReworkDelta.ToString(CultureInfo.InvariantCulture),
            pair.ReferenceEvidenceId?.ToString("N") ?? string.Empty,
            pair.CandidateEvidenceId?.ToString("N") ?? string.Empty,
            pair.Failure)));
        return string.Join("\n", rows) + "\n";
    }

    public static string Analysis(ExperimentReport report)
    {
        var rows = new List<string>
        {
            Row(
                "hypothesis_id", "conclusion", "conclusion_reason", "variant_id",
                "context_strategy", "planned_runs", "observed_runs", "completed_runs",
                "failed_runs", "skipped_runs", "vcc", "vcc_rate", "first_pass_vcc",
                "first_pass_rate", "failure_rate", "scope_violations",
                "scope_violation_rate", "rework_attempts", "total_estimated_cost",
                "vcc_per_estimated_dollar", "token_mean", "token_ci95_lower",
                "token_ci95_upper", "latency_median_seconds")
        };
        if (report.Analysis is null)
            return string.Join("\n", rows) + "\n";

        rows.AddRange(report.Analysis.Variants.Select(variant => Row(
            report.Analysis.HypothesisId,
            report.Analysis.Conclusion.ToString(),
            report.Analysis.ConclusionReason,
            variant.VariantId,
            variant.ContextStrategy,
            variant.PlannedRuns.ToString(CultureInfo.InvariantCulture),
            variant.ObservedRuns.ToString(CultureInfo.InvariantCulture),
            variant.CompletedRuns.ToString(CultureInfo.InvariantCulture),
            variant.FailedRuns.ToString(CultureInfo.InvariantCulture),
            variant.SkippedRuns.ToString(CultureInfo.InvariantCulture),
            variant.VerifiedCodeChanges.ToString(CultureInfo.InvariantCulture),
            variant.VerifiedCodeChangeRate.ToString("F6", CultureInfo.InvariantCulture),
            variant.FirstPassVerifiedChanges.ToString(CultureInfo.InvariantCulture),
            variant.FirstPassRate.ToString("F6", CultureInfo.InvariantCulture),
            variant.FailureRate.ToString("F6", CultureInfo.InvariantCulture),
            variant.ScopeViolations.ToString(CultureInfo.InvariantCulture),
            variant.ScopeViolationRate.ToString("F6", CultureInfo.InvariantCulture),
            variant.ReworkAttempts.ToString(CultureInfo.InvariantCulture),
            variant.TotalEstimatedCost.ToString(CultureInfo.InvariantCulture),
            Number(variant.VerifiedChangesPerEstimatedDollar),
            Number(variant.TotalTokens.Mean),
            Number(variant.TotalTokens.MeanConfidenceIntervalLower),
            Number(variant.TotalTokens.MeanConfidenceIntervalUpper),
            Number(variant.LatencySeconds.Median))));
        return string.Join("\n", rows) + "\n";
    }

    public static string CostRecords(ExperimentReport report)
    {
        var rows = new List<string>
        {
            Row(
                "run_key", "evidence_id", "status", "decision", "included_in_cpvc", "inclusion_reason",
                "vcc", "vcc_reason", "model", "risk", "task_id", "strategy", "period_utc",
                "estimated_input_tokens", "reserved_output_tokens", "provider_input_tokens",
                "provider_output_tokens", "provider_request_id", "input_token_divergence",
                "output_token_divergence",
                "rate_card_estimated_cost_usd", "local_resource_estimated_cost_usd",
                "reconciled_cost_usd", "effective_cost_usd", "cost_basis", "cost_complete",
                "pricing_table_version", "pricing_table_hash", "pricing_effective_date",
                "pricing_source", "pricing_rate_kind", "rate_card_usage_basis",
                "local_cost_policy_version", "local_power_watts",
                "local_electricity_usd_per_kwh", "local_hardware_cost_usd",
                "local_hardware_lifetime_hours", "retry_count",
                "accounting_component_count", "fallback_used", "reconciliation_source",
                "reconciliation_reference")
        };
        rows.AddRange(report.CostRecords.Select(record => Row(
            record.RunKey,
            record.EvidenceId?.ToString("N") ?? string.Empty,
            record.Status.ToString(),
            record.Decision?.ToString() ?? string.Empty,
            record.IncludedInCpvc.ToString(CultureInfo.InvariantCulture),
            record.InclusionReason,
            record.VerifiedCodeChange.ToString(CultureInfo.InvariantCulture),
            record.VccReason,
            record.Model,
            record.Risk.ToString(),
            record.TaskId,
            record.Strategy,
            record.PeriodUtc,
            Integer(record.EstimatedInputTokens),
            Integer(record.ReservedOutputTokens),
            Integer(record.ProviderInputTokens),
            Integer(record.ProviderOutputTokens),
            record.ProviderRequestId,
            Integer(record.InputTokenDivergence),
            Integer(record.OutputTokenDivergence),
            Decimal(record.RateCardEstimatedCostUsd),
            Decimal(record.LocalResourceEstimatedCostUsd),
            Decimal(record.ReconciledCostUsd),
            Decimal(record.EffectiveCostUsd),
            record.CostBasis,
            record.CostComplete.ToString(CultureInfo.InvariantCulture),
            record.PricingTableVersion,
            record.PricingTableHash,
            record.PricingEffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ??
                string.Empty,
            record.PricingSource,
            record.PricingRateKind,
            record.RateCardUsageBasis,
            record.LocalCostPolicyVersion,
            Decimal(record.LocalPowerWatts),
            Decimal(record.LocalElectricityUsdPerKwh),
            Decimal(record.LocalHardwareCostUsd),
            Decimal(record.LocalHardwareLifetimeHours),
            record.RetryCount.ToString(CultureInfo.InvariantCulture),
            record.AccountingComponentCount.ToString(CultureInfo.InvariantCulture),
            record.FallbackUsed.ToString(CultureInfo.InvariantCulture),
            record.ReconciliationSource,
            record.ReconciliationReference)));
        return string.Join("\n", rows) + "\n";
    }

    public static string CostEfficiency(ExperimentReport report)
    {
        var rows = new List<string>
        {
            Row(
                "schema_version", "currency", "vcc_definition_version", "dimension", "value",
                "planned_members", "sample_size", "completed_count", "rejected_count",
                "failed_count", "skipped_count", "vcc", "known_cost_count",
                "missing_cost_count", "cost_coverage", "total_effective_cost_usd", "cpvc_usd",
                "cpvc_ci95_lower", "cpvc_ci95_upper", "cost_mean", "cost_stddev",
                "cost_median", "member_run_keys", "included_run_keys", "evidence_ids")
        };
        rows.AddRange(report.CostEfficiency.Aggregates.Select(aggregate => Row(
            report.CostEfficiency.SchemaVersion,
            report.CostEfficiency.Currency,
            report.CostEfficiency.VccDefinitionVersion,
            aggregate.Dimension,
            aggregate.Value,
            aggregate.PlannedMembers.ToString(CultureInfo.InvariantCulture),
            aggregate.SampleSize.ToString(CultureInfo.InvariantCulture),
            aggregate.CompletedCount.ToString(CultureInfo.InvariantCulture),
            aggregate.RejectedCount.ToString(CultureInfo.InvariantCulture),
            aggregate.FailedCount.ToString(CultureInfo.InvariantCulture),
            aggregate.SkippedCount.ToString(CultureInfo.InvariantCulture),
            aggregate.VerifiedCodeChanges.ToString(CultureInfo.InvariantCulture),
            aggregate.KnownCostCount.ToString(CultureInfo.InvariantCulture),
            aggregate.MissingCostCount.ToString(CultureInfo.InvariantCulture),
            aggregate.CostCoverage.ToString("G17", CultureInfo.InvariantCulture),
            Decimal(aggregate.TotalEffectiveCostUsd),
            Decimal(aggregate.CpvcUsd),
            Number(aggregate.CpvcConfidenceIntervalLower),
            Number(aggregate.CpvcConfidenceIntervalUpper),
            Number(aggregate.EffectiveCostDistribution.Mean),
            Number(aggregate.EffectiveCostDistribution.StandardDeviation),
            Number(aggregate.EffectiveCostDistribution.Median),
            string.Join(';', aggregate.MemberRunKeys),
            string.Join(';', aggregate.IncludedRunKeys),
            string.Join(';', aggregate.EvidenceIds.Select(id => id.ToString("N"))))));
        return string.Join("\n", rows) + "\n";
    }

    private static string Number(double? value) =>
        value?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Decimal(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Integer(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Row(params string[] values) => string.Join(',', values.Select(Escape));

    private static string Escape(string value)
    {
        if (!value.ContainsAny(',', '"', '\r', '\n'))
            return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static bool ContainsAny(this string value, params char[] characters) =>
        value.IndexOfAny(characters) >= 0;
}
