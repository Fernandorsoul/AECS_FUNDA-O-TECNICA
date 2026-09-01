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
                "context_strategy_version", "context_max_tokens", "context_max_characters",
                "context_max_file_tokens", "context_max_file_characters",
                "context_dependency_depth", "seed", "parameters_json", "baseline_commit",
                "expected_decision",
                "actual_decision", "matches_expected", "duration_seconds", "input_tokens",
                "output_tokens", "estimated_cost", "files_changed", "retry_count",
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
                "candidate_status", "both_completed", "decision_changed",
                "duration_delta_seconds", "cost_delta", "reference_evidence_id",
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
            pair.BothCompleted.ToString(CultureInfo.InvariantCulture),
            pair.DecisionChanged.ToString(CultureInfo.InvariantCulture),
            pair.DurationDeltaSeconds.ToString("F6", CultureInfo.InvariantCulture),
            pair.CostDelta.ToString(CultureInfo.InvariantCulture),
            pair.ReferenceEvidenceId?.ToString("N") ?? string.Empty,
            pair.CandidateEvidenceId?.ToString("N") ?? string.Empty,
            pair.Failure)));
        return string.Join("\n", rows) + "\n";
    }

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
