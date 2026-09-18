using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Application.ContextCompiler;
using AECS.Domain.Enums;

namespace AECS.Application.Experiments;

public static class ExperimentDatasetSchema
{
    public const string Version = "aecs.experiment-dataset/v1";
    public const string ContextAbVersion = "aecs.experiment-dataset/v2";
    public const string ContextAbIntegrityVersion = "aecs.experiment-dataset/v3";
    public const string ContextAbDecisionPolicyVersion = "aecs.experiment-dataset/v4";
    public const string FactorialLedgerContextAbVersion = "aecs.experiment-dataset/v5";
    public const string ReportVersion = "aecs.experiment-report/v5";
    public const string CheckpointVersion = "aecs.experiment-checkpoint/v1";
}

public static class ExperimentDesigns
{
    public const string PairedContextAb = "paired-context-ab";
    public const string FactorialLedgerContext2x2 = "factorial-ledger-context-2x2";
    public const string VccPerEstimatedCost = "vcc-per-estimated-cost";
}

public enum HypothesisConclusion
{
    NotEvaluated,
    Maintain,
    Adjust,
    Abandon
}

public enum ZeroReferencePolicy
{
    Adjust,
    AbsolutePairedDelta
}

public enum ExperimentProvider
{
    Mock,
    Local,
    Cloud
}

public enum ExperimentResultStatus
{
    Completed,
    Failed,
    Skipped
}

public sealed class ExperimentDatasetManifest
{
    public string SchemaVersion { get; init; } = ExperimentDatasetSchema.Version;
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public ExperimentRepositoryDefinition Repository { get; init; } = new();
    public int Repetitions { get; init; } = 1;
    public string ReferenceVariantId { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExperimentProtocol? Protocol { get; init; }
    public List<ExperimentTaskDefinition> Tasks { get; init; } = [];
    public List<ExperimentVariantDefinition> Variants { get; init; } = [];
}

public sealed class ExperimentProtocol
{
    public string Design { get; init; } = string.Empty;
    public string HypothesisId { get; init; } = string.Empty;
    public string Hypothesis { get; init; } = string.Empty;
    public string PrimaryMetric { get; init; } = string.Empty;
    public int MinimumPairedSamples { get; init; }
    public double ConfidenceLevel { get; init; } = 0.95;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ZeroReferencePolicy? ZeroReferencePolicy { get; init; }
    public ExperimentDeathCriteria DeathCriteria { get; init; } = new();
}

public sealed class ExperimentDeathCriteria
{
    public double MinimumRelativeImprovement { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinimumAbsoluteImprovement { get; init; }
    public double MaximumCandidateFailureRate { get; init; }
    public double MaximumCandidateScopeViolationRate { get; init; }
}

public sealed class ExperimentRepositoryDefinition
{
    public string Path { get; init; } = string.Empty;
    public string Baseline { get; init; } = string.Empty;
}

public sealed class ExperimentTaskDefinition
{
    public string Id { get; init; } = string.Empty;
    public string ContractPath { get; init; } = string.Empty;
    public TaskDecision ExpectedDecision { get; init; }
    public List<string> RequiredContextPaths { get; init; } = [];
}

public sealed class ExperimentVariantDefinition
{
    public string Id { get; init; } = string.Empty;
    public ExperimentProvider Provider { get; init; }
    public string Model { get; init; } = string.Empty;
    public string ContextStrategy { get; init; } = string.Empty;
    public ContextCompilationOptions Context { get; init; } = new();
    public bool RequiresRealProvider { get; init; }
    public bool ConstraintLedgerEnabled { get; init; } = true;
    public int? Seed { get; init; }
    public Dictionary<string, string> Parameters { get; init; } =
        new(StringComparer.Ordinal);
}

public sealed class LoadedExperimentDataset
{
    public string ManifestPath { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public ExperimentDatasetManifest Manifest { get; init; } = new();

    public string ContractPath(ExperimentTaskDefinition task) => System.IO.Path.GetFullPath(
        System.IO.Path.Combine(RepositoryPath, task.ContractPath.Replace('/', Path.DirectorySeparatorChar)));
}

public sealed class ExperimentRunDefinition
{
    public string DatasetId { get; init; } = string.Empty;
    public string DatasetVersion { get; init; } = string.Empty;
    public string DatasetHash { get; init; } = string.Empty;
    public string RunKey { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public ExperimentTaskDefinition Task { get; init; } = new();
    public string ContractPath { get; init; } = string.Empty;
    public ExperimentVariantDefinition Variant { get; init; } = new();
    public int Repetition { get; init; }
    public int? EffectiveSeed { get; init; }
}

public sealed class ExperimentEnvironment
{
    public string HarnessVersion { get; init; } = string.Empty;
    public string HarnessRevision { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string OsArchitecture { get; init; } = string.Empty;
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string RuntimeIdentifier { get; init; } = string.Empty;

    public static ExperimentEnvironment Capture() => new()
    {
        HarnessVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
            "unknown",
        HarnessRevision = Environment.GetEnvironmentVariable("AECS_BUILD_VERSION")
            ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
            ?? Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
            ?? "unknown",
        Framework = RuntimeInformation.FrameworkDescription,
        OperatingSystem = RuntimeInformation.OSDescription,
        OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier
    };
}

public sealed class ExperimentPairedComparison
{
    public string TaskId { get; init; } = string.Empty;
    public int Repetition { get; init; }
    public string ReferenceVariantId { get; init; } = string.Empty;
    public string CandidateVariantId { get; init; } = string.Empty;
    public string ReferenceRunKey { get; init; } = string.Empty;
    public string CandidateRunKey { get; init; } = string.Empty;
    public List<ExperimentContextFile> ReferenceIncludedContext { get; init; } = [];
    public List<ExperimentContextFile> CandidateIncludedContext { get; init; } = [];
    public bool EffectiveContextChanged { get; init; }
    public ExperimentResultStatus ReferenceStatus { get; init; }
    public ExperimentResultStatus CandidateStatus { get; init; }
    public bool BothCompleted { get; init; }
    public bool DecisionChanged { get; init; }
    public bool ReferenceVerifiedCodeChange { get; init; }
    public bool CandidateVerifiedCodeChange { get; init; }
    public int VerifiedCodeChangeDelta { get; init; }
    public bool ReferenceFirstPass { get; init; }
    public bool CandidateFirstPass { get; init; }
    public int FirstPassDelta { get; init; }
    public int ReferenceTotalTokens { get; init; }
    public int CandidateTotalTokens { get; init; }
    public int TotalTokenDelta { get; init; }
    public decimal ReferenceEstimatedCost { get; init; }
    public decimal CandidateEstimatedCost { get; init; }
    public double DurationDeltaSeconds { get; init; }
    public decimal CostDelta { get; init; }
    public int ScopeViolationDelta { get; init; }
    public int ReworkDelta { get; init; }
    public Guid? ReferenceEvidenceId { get; init; }
    public Guid? CandidateEvidenceId { get; init; }
    public string Failure { get; init; } = string.Empty;
}

public static class ExperimentDatasetLoader
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static LoadedExperimentDataset Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        using var document = JsonDocument.Parse(
            File.ReadAllText(fullManifestPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        RejectDuplicateProperties(document.RootElement);
        var manifest = document.RootElement.Deserialize<ExperimentDatasetManifest>(Options) ??
            throw new InvalidOperationException("Experiment dataset manifest is empty.");
        ExperimentDatasetContract.Validate(manifest);

        var manifestDirectory = Path.GetDirectoryName(fullManifestPath)!;
        var repositoryPath = ResolveWithin(
            manifestDirectory,
            manifest.Repository.Path,
            "dataset repository");
        if (!Directory.Exists(repositoryPath))
            throw new DirectoryNotFoundException($"Dataset repository was not found: {repositoryPath}");
        foreach (var task in manifest.Tasks)
        {
            var contractPath = ResolveWithin(repositoryPath, task.ContractPath, "task contract");
            if (!File.Exists(contractPath))
                throw new FileNotFoundException("Dataset task contract was not found.", contractPath);
        }

        return new LoadedExperimentDataset
        {
            ManifestPath = fullManifestPath,
            RepositoryPath = repositoryPath,
            Manifest = manifest
        };
    }

    public static JsonSerializerOptions SerializerOptions => Options;

    private static string ResolveWithin(string root, string relativePath, string description)
    {
        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(
            resolvedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(
                resolvedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {description} escapes its allowed root.");
        }
        return resolved;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException(
                        $"Duplicate dataset property '{property.Name}' is not allowed.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class ExperimentDatasetContract
{
    public static void Validate(ExperimentDatasetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion is not (
                ExperimentDatasetSchema.Version or
                ExperimentDatasetSchema.ContextAbVersion or
                ExperimentDatasetSchema.ContextAbIntegrityVersion or
                ExperimentDatasetSchema.ContextAbDecisionPolicyVersion or
                ExperimentDatasetSchema.FactorialLedgerContextAbVersion) ||
            !ValidId(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Version.Length > 100 ||
            manifest.Repository is null ||
            !SafeRelativePath(manifest.Repository.Path, allowCurrentDirectory: true) ||
            string.IsNullOrWhiteSpace(manifest.Repository.Baseline) ||
            manifest.Repository.Baseline.Length > 200 ||
            manifest.Repetitions is < 1 or > 100 ||
            manifest.Tasks is null || manifest.Tasks.Count == 0 ||
            manifest.Variants is null || manifest.Variants.Count == 0 ||
            manifest.Tasks.Any(InvalidTask) ||
            manifest.Variants.Any(InvalidVariant) ||
            HasDuplicateIds(manifest.Tasks.Select(task => task.Id)) ||
            HasDuplicateIds(manifest.Variants.Select(variant => variant.Id)) ||
            !manifest.Variants.Any(variant => variant.Id.Equals(
                manifest.ReferenceVariantId,
                StringComparison.Ordinal)) ||
            manifest.SchemaVersion == ExperimentDatasetSchema.Version &&
            manifest.Protocol is not null ||
            (manifest.SchemaVersion == ExperimentDatasetSchema.ContextAbVersion ||
             manifest.SchemaVersion == ExperimentDatasetSchema.ContextAbIntegrityVersion ||
             manifest.SchemaVersion == ExperimentDatasetSchema.ContextAbDecisionPolicyVersion) &&
            InvalidContextAbProtocol(manifest) ||
            manifest.SchemaVersion == ExperimentDatasetSchema.FactorialLedgerContextAbVersion &&
            InvalidFactorialProtocol(manifest))
        {
            throw new InvalidOperationException(
                "Experiment dataset is incomplete, unsafe, or uses an unsupported schema.");
        }
    }

    private static bool InvalidFactorialProtocol(ExperimentDatasetManifest manifest)
    {
        var protocol = manifest.Protocol;
        if (protocol is null ||
            protocol.Design != ExperimentDesigns.FactorialLedgerContext2x2 ||
            string.IsNullOrWhiteSpace(protocol.HypothesisId) || protocol.HypothesisId.Length > 100 ||
            string.IsNullOrWhiteSpace(protocol.Hypothesis) || protocol.Hypothesis.Length > 1000 ||
            protocol.PrimaryMetric != ExperimentDesigns.VccPerEstimatedCost ||
            protocol.MinimumPairedSamples < 2 ||
            protocol.ConfidenceLevel != 0.95 ||
            protocol.DeathCriteria is null ||
            !double.IsFinite(protocol.DeathCriteria.MinimumRelativeImprovement) ||
            protocol.DeathCriteria.MinimumRelativeImprovement is < -1 or > 10 ||
            !double.IsFinite(protocol.DeathCriteria.MaximumCandidateFailureRate) ||
            protocol.DeathCriteria.MaximumCandidateFailureRate is < 0 or > 1 ||
            !double.IsFinite(protocol.DeathCriteria.MaximumCandidateScopeViolationRate) ||
            protocol.DeathCriteria.MaximumCandidateScopeViolationRate is < 0 or > 1 ||
            manifest.Variants.Count != 4 ||
            !manifest.ReferenceVariantId.Equals("A", StringComparison.Ordinal) ||
            manifest.Tasks.Count * manifest.Repetitions < protocol.MinimumPairedSamples)
        {
            return true;
        }

        var byId = manifest.Variants.ToDictionary(
            variant => variant.Id,
            StringComparer.Ordinal);
        if (!byId.ContainsKey("A") || !byId.ContainsKey("B") ||
            !byId.ContainsKey("C") || !byId.ContainsKey("D"))
        {
            return true;
        }

        var armA = byId["A"];
        var armB = byId["B"];
        var armC = byId["C"];
        var armD = byId["D"];
        var arms = new[] { armA, armB, armC, armD };
        return
            // A = baseline harness, ledger off; B = baseline harness, ledger on;
            // C = graph-ranked harness, ledger off; D = graph-ranked harness, ledger on.
            armA.ConstraintLedgerEnabled ||
            armA.ContextStrategy != ContextStrategyIds.NaivePathOrder ||
            !armB.ConstraintLedgerEnabled ||
            armB.ContextStrategy != ContextStrategyIds.NaivePathOrder ||
            armC.ConstraintLedgerEnabled ||
            armC.ContextStrategy != ContextStrategyIds.GraphRanked ||
            !armD.ConstraintLedgerEnabled ||
            armD.ContextStrategy != ContextStrategyIds.GraphRanked ||
            arms.Any(variant => variant.Provider != armA.Provider ||
                !variant.Model.Equals(armA.Model, StringComparison.Ordinal) ||
                variant.RequiresRealProvider != armA.RequiresRealProvider ||
                variant.Seed != armA.Seed ||
                !Equivalent(variant.Context, armA.Context) ||
                !Equivalent(variant.Parameters, armA.Parameters));
    }

    private static bool InvalidContextAbProtocol(ExperimentDatasetManifest manifest)
    {
        var protocol = manifest.Protocol;
        if (protocol is null ||
            protocol.Design != ExperimentDesigns.PairedContextAb ||
            !protocol.HypothesisId.Equals("H1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(protocol.Hypothesis) || protocol.Hypothesis.Length > 1000 ||
            protocol.PrimaryMetric != ExperimentDesigns.VccPerEstimatedCost ||
            protocol.MinimumPairedSamples < 2 ||
            protocol.ConfidenceLevel != 0.95 ||
            protocol.DeathCriteria is null ||
            !double.IsFinite(protocol.DeathCriteria.MinimumRelativeImprovement) ||
            protocol.DeathCriteria.MinimumRelativeImprovement is < -1 or > 10 ||
            !double.IsFinite(protocol.DeathCriteria.MaximumCandidateFailureRate) ||
            protocol.DeathCriteria.MaximumCandidateFailureRate is < 0 or > 1 ||
            !double.IsFinite(protocol.DeathCriteria.MaximumCandidateScopeViolationRate) ||
            protocol.DeathCriteria.MaximumCandidateScopeViolationRate is < 0 or > 1 ||
            InvalidDecisionPolicy(manifest.SchemaVersion, protocol) ||
            manifest.Variants.Count != 2 ||
            manifest.Tasks.Count * manifest.Repetitions < protocol.MinimumPairedSamples ||
            manifest.Variants.Any(variant =>
                !ContextStrategyIds.IsSupported(variant.ContextStrategy)) ||
            manifest.Variants.Count(variant =>
                variant.ContextStrategy == ContextStrategyIds.GraphRanked) != 1 ||
            manifest.Variants.Count(variant =>
                variant.ContextStrategy == ContextStrategyIds.NaivePathOrder) != 1)
        {
            return true;
        }

        if (manifest.SchemaVersion is (
                ExperimentDatasetSchema.ContextAbIntegrityVersion or
                ExperimentDatasetSchema.ContextAbDecisionPolicyVersion) &&
            manifest.Tasks.Any(task => task.RequiredContextPaths.Count == 0))
        {
            return true;
        }

        var reference = manifest.Variants.Single(variant => variant.Id.Equals(
            manifest.ReferenceVariantId,
            StringComparison.Ordinal));
        var candidate = manifest.Variants.Single(variant => !variant.Id.Equals(
            manifest.ReferenceVariantId,
            StringComparison.Ordinal));
        return reference.ContextStrategy != ContextStrategyIds.NaivePathOrder ||
            candidate.ContextStrategy != ContextStrategyIds.GraphRanked ||
            reference.Provider != candidate.Provider ||
            !reference.Model.Equals(candidate.Model, StringComparison.Ordinal) ||
            reference.RequiresRealProvider != candidate.RequiresRealProvider ||
            reference.Seed != candidate.Seed ||
            !Equivalent(reference.Context, candidate.Context) ||
            !Equivalent(reference.Parameters, candidate.Parameters);
    }

    private static bool InvalidDecisionPolicy(
        string schemaVersion,
        ExperimentProtocol protocol)
    {
        var isDecisionPolicyVersion = schemaVersion ==
            ExperimentDatasetSchema.ContextAbDecisionPolicyVersion;
        if (!isDecisionPolicyVersion)
        {
            return protocol.ZeroReferencePolicy is not null ||
                protocol.DeathCriteria.MinimumAbsoluteImprovement is not null;
        }

        if (protocol.ZeroReferencePolicy is null ||
            !Enum.IsDefined(protocol.ZeroReferencePolicy.Value))
        {
            return true;
        }

        var minimumAbsolute = protocol.DeathCriteria.MinimumAbsoluteImprovement;
        return protocol.ZeroReferencePolicy.Value switch
        {
            ZeroReferencePolicy.Adjust => minimumAbsolute is not null,
            ZeroReferencePolicy.AbsolutePairedDelta => minimumAbsolute is null ||
                !double.IsFinite(minimumAbsolute.Value) || minimumAbsolute.Value < 0,
            _ => true
        };
    }

    private static bool Equivalent(ContextCompilationOptions left, ContextCompilationOptions right) =>
        left.MaxTokens == right.MaxTokens &&
        left.MaxCharacters == right.MaxCharacters &&
        left.MaxFileCharacters == right.MaxFileCharacters &&
        left.MaxFileTokens == right.MaxFileTokens &&
        left.DependencyDepth == right.DependencyDepth;

    private static bool Equivalent(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(parameter =>
            right.TryGetValue(parameter.Key, out var value) &&
            value.Equals(parameter.Value, StringComparison.Ordinal));

    public static bool BaselineMatches(string expected, string actual) =>
        expected.Equals("HEAD", StringComparison.Ordinal) ||
        actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        expected.Length >= 7 && actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);

    private static bool InvalidTask(ExperimentTaskDefinition task) =>
        task is null || !ValidId(task.Id) || !SafeRelativePath(task.ContractPath) ||
        !Enum.IsDefined(task.ExpectedDecision) ||
        task.RequiredContextPaths is null || task.RequiredContextPaths.Count > 50 ||
        task.RequiredContextPaths.Any(path => !SafeRelativePath(path)) ||
        task.RequiredContextPaths.Distinct(StringComparer.Ordinal).Count() !=
            task.RequiredContextPaths.Count;

    private static bool InvalidVariant(ExperimentVariantDefinition variant) =>
        variant is null ||
        !ValidId(variant.Id) ||
        !Enum.IsDefined(variant.Provider) ||
        string.IsNullOrWhiteSpace(variant.Model) || variant.Model.Length > 200 ||
        string.IsNullOrWhiteSpace(variant.ContextStrategy) ||
        variant.ContextStrategy.Length > 200 ||
        variant.Context is null ||
        variant.Context.MaxTokens <= 0 ||
        variant.Context.MaxTokens > 1_000_000 ||
        variant.Context.MaxCharacters <= 0 ||
        variant.Context.MaxCharacters > 4_000_000 ||
        variant.Context.MaxFileCharacters <= 0 ||
        variant.Context.MaxFileCharacters > 1_000_000 ||
        variant.Context.MaxFileTokens <= 0 ||
        variant.Context.MaxFileTokens > 250_000 ||
        variant.Context.DependencyDepth is < 0 or > 10 ||
        variant.Context.MaxFileTokens > variant.Context.MaxTokens ||
        variant.Context.MaxFileCharacters > variant.Context.MaxCharacters ||
        variant.Provider == ExperimentProvider.Mock && variant.RequiresRealProvider ||
        variant.Provider == ExperimentProvider.Mock && variant.Seed is not null ||
        variant.Provider != ExperimentProvider.Mock && !variant.RequiresRealProvider ||
        variant.Seed is < 0 or > int.MaxValue - 100 ||
        variant.Parameters is null ||
        variant.Parameters.Any(parameter =>
            string.IsNullOrWhiteSpace(parameter.Key) || parameter.Key.Length > 100 ||
            parameter.Value is null || parameter.Value.Length > 1000) ||
        variant.Parameters.Keys.Any(parameter => !SupportedParameters(variant.Provider)
            .Contains(parameter, StringComparer.Ordinal)) ||
        variant.Parameters.Any(InvalidParameterValue);

    private static IReadOnlyCollection<string> SupportedParameters(
        ExperimentProvider provider) => provider switch
        {
            ExperimentProvider.Mock => [],
            ExperimentProvider.Local => ["baseUrl", "contextWindowTokens"],
            ExperimentProvider.Cloud =>
                ["baseUrl", "contextWindowTokens", "maxOutputTokens", "temperature"],
            _ => []
        };

    private static bool InvalidParameterValue(KeyValuePair<string, string> parameter) =>
        parameter.Key switch
        {
            "baseUrl" => !Uri.TryCreate(parameter.Value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"),
            "contextWindowTokens" => !int.TryParse(
                    parameter.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var contextWindow) || contextWindow < 1024,
            "maxOutputTokens" => !int.TryParse(
                    parameter.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var maxOutput) || maxOutput < 1,
            "temperature" => !double.TryParse(
                    parameter.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var temperature) || temperature is < 0 or > 2,
            _ => true
        };

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool SafeRelativePath(string value, bool allowCurrentDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Contains('\\') || value.StartsWith('/'))
        {
            return false;
        }
        if (allowCurrentDirectory && value == ".")
            return true;
        return value.Split('/').All(segment => segment is not ("" or "." or ".."));
    }

    private static bool HasDuplicateIds(IEnumerable<string> ids) =>
        ids.Distinct(StringComparer.Ordinal).Count() != ids.Count();
}

public static class ExperimentDatasetFingerprint
{
    public static string Create(ExperimentDatasetManifest manifest, string baselineCommit)
    {
        ExperimentDatasetContract.Validate(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineCommit);
        var json = JsonSerializer.Serialize(manifest, ExperimentDatasetLoader.SerializerOptions);
        return Hash(json + "\nresolved-baseline:" + baselineCommit.ToLowerInvariant());
    }

    public static string RunKey(
        string datasetHash,
        string taskId,
        string variantId,
        int repetition) => Hash(
            $"{datasetHash}\n{taskId}\n{variantId}\n{repetition}")[7..];

    public static Guid ExperimentId(string datasetHash)
    {
        var bytes = Convert.FromHexString(datasetHash[7..39]);
        return new Guid(bytes);
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
