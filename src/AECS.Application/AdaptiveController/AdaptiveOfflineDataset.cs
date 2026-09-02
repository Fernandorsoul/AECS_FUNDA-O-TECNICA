using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Application.Experiments;
using AECS.Domain.Enums;

namespace AECS.Application.AdaptiveController;

public static class AdaptiveOfflineSchema
{
    public const string DatasetVersion = "aecs.adaptive-offline-dataset/v1";
    public const string SessionVersion = "aecs.adaptive-offline-session/v1";
    public const string CheckpointVersion = "aecs.adaptive-offline-checkpoint/v1";
    public const string ReportVersion = "aecs.adaptive-offline-report/v1";
    public const string Design = "paired-adaptive-routing";
    public const string HypothesisId = "H2";
    public const string PrimaryMetric = "vcc-per-effective-cost";
}

public enum AdaptiveOfflineProvider
{
    Local,
    Cloud
}

public sealed class AdaptiveOfflineDatasetManifest
{
    [JsonRequired]
    public string SchemaVersion { get; init; } = string.Empty;
    [JsonRequired]
    public string Id { get; init; } = string.Empty;
    [JsonRequired]
    public string Version { get; init; } = string.Empty;
    [JsonRequired]
    public DateTime PreregisteredAtUtc { get; init; }
    [JsonRequired]
    public AdaptiveOfflineRepositoryDefinition Repository { get; init; } = new();
    [JsonRequired]
    public AdaptiveOfflineProviderDefinition Provider { get; init; } = new();
    [JsonRequired]
    public int Repetitions { get; init; } = 1;
    [JsonRequired]
    public AdaptiveOfflineProtocol Protocol { get; init; } = new();
    [JsonRequired]
    public List<AdaptiveOfflineTaskDefinition> Tasks { get; init; } = [];
}

public sealed class AdaptiveOfflineRepositoryDefinition
{
    [JsonRequired]
    public string Path { get; init; } = string.Empty;
    [JsonRequired]
    public string Baseline { get; init; } = string.Empty;
    [JsonRequired]
    public string SourceUri { get; init; } = string.Empty;
    [JsonRequired]
    public string LicenseSpdx { get; init; } = string.Empty;
    [JsonRequired]
    public bool RedistributionAllowed { get; init; }
}

public sealed class AdaptiveOfflineProviderDefinition
{
    [JsonRequired]
    public AdaptiveOfflineProvider Kind { get; init; }
    [JsonRequired]
    public Dictionary<string, string> Parameters { get; init; } =
        new(StringComparer.Ordinal);
}

public sealed class AdaptiveOfflineProtocol
{
    [JsonRequired]
    public string Design { get; init; } = string.Empty;
    [JsonRequired]
    public string HypothesisId { get; init; } = string.Empty;
    [JsonRequired]
    public string Hypothesis { get; init; } = string.Empty;
    [JsonRequired]
    public string PrimaryMetric { get; init; } = string.Empty;
    [JsonRequired]
    public int MinimumDistinctTasks { get; init; }
    [JsonRequired]
    public double ConfidenceLevel { get; init; }
    [JsonRequired]
    public ZeroReferencePolicy ZeroReferencePolicy { get; init; }
    [JsonRequired]
    public AdaptiveOfflineDeathCriteria DeathCriteria { get; init; } = new();
}

public sealed class AdaptiveOfflineDeathCriteria
{
    [JsonRequired]
    public double MinimumRelativeImprovement { get; init; }
    [JsonRequired]
    public double MinimumAbsoluteImprovement { get; init; }
    [JsonRequired]
    public double MaximumCandidateFailureRate { get; init; }
    [JsonRequired]
    public double MaximumCandidateScopeViolationRate { get; init; }
    [JsonRequired]
    public double MaximumCandidateSecurityViolationRate { get; init; }
    [JsonRequired]
    public double MaximumMedianLatencyRegressionRate { get; init; }
}

public sealed class AdaptiveOfflineTaskDefinition
{
    [JsonRequired]
    public string Id { get; init; } = string.Empty;
    [JsonRequired]
    public string ContractPath { get; init; } = string.Empty;
    [JsonRequired]
    public TaskDecision ExpectedDecision { get; init; }
    [JsonRequired]
    public int Seed { get; init; }
    [JsonRequired]
    public DateTime HistoryCutoffUtc { get; init; }
    [JsonRequired]
    public Guid RecommendationEvidenceId { get; init; }
    [JsonRequired]
    public string RecommendationEvidenceHash { get; init; } = string.Empty;
}

public sealed class LoadedAdaptiveOfflineDataset
{
    public string ManifestPath { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public AdaptiveOfflineDatasetManifest Manifest { get; init; } = new();

    public string ContractPath(AdaptiveOfflineTaskDefinition task) => Path.GetFullPath(
        Path.Combine(
            RepositoryPath,
            task.ContractPath.Replace('/', Path.DirectorySeparatorChar)));
}

public static class AdaptiveOfflineDatasetLoader
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static LoadedAdaptiveOfflineDataset Load(string manifestPath)
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
        var manifest = document.RootElement.Deserialize<AdaptiveOfflineDatasetManifest>(Options) ??
            throw new InvalidOperationException("Adaptive offline dataset manifest is empty.");
        AdaptiveOfflineDatasetContract.Validate(manifest);

        var manifestDirectory = Path.GetDirectoryName(fullManifestPath)!;
        var repositoryPath = ResolveWithin(
            manifestDirectory,
            manifest.Repository.Path,
            "adaptive offline repository");
        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Adaptive offline repository was not found: {repositoryPath}");
        }

        foreach (var task in manifest.Tasks)
        {
            var contractPath = ResolveWithin(
                repositoryPath,
                task.ContractPath,
                "adaptive offline task contract");
            if (!File.Exists(contractPath))
            {
                throw new FileNotFoundException(
                    "Adaptive offline task contract was not found.",
                    contractPath);
            }
        }

        return new LoadedAdaptiveOfflineDataset
        {
            ManifestPath = fullManifestPath,
            RepositoryPath = repositoryPath,
            Manifest = manifest
        };
    }

    public static JsonSerializerOptions SerializerOptions => Options;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null,
            allowIntegerValues: false));
        return options;
    }

    private static string ResolveWithin(string root, string relativePath, string description)
    {
        var resolvedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException(
                        $"Duplicate adaptive offline property '{property.Name}' is not allowed.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}

public static class AdaptiveOfflineDatasetContract
{
    public static void Validate(AdaptiveOfflineDatasetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != AdaptiveOfflineSchema.DatasetVersion ||
            !ValidId(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 100 ||
            !IsUtc(manifest.PreregisteredAtUtc) ||
            InvalidRepository(manifest.Repository) ||
            InvalidProvider(manifest.Provider) ||
            manifest.Repetitions is < 1 or > 20 ||
            InvalidProtocol(manifest.Protocol) ||
            manifest.Tasks is null ||
            manifest.Tasks.Count < manifest.Protocol.MinimumDistinctTasks ||
            manifest.Tasks.Count > 500 ||
            manifest.Tasks.Any(task => InvalidTask(task, manifest.PreregisteredAtUtc)) ||
            manifest.Tasks.Select(task => task.Id)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Tasks.Count ||
            manifest.Tasks.Select(task => task.ContractPath)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Tasks.Count ||
            manifest.Tasks.Select(task => task.RecommendationEvidenceId)
                .Distinct().Count() != manifest.Tasks.Count)
        {
            throw new InvalidOperationException(
                "Adaptive offline dataset is incomplete, unsafe, or uses an unsupported schema.");
        }
    }

    public static bool BaselineMatches(string expected, string actual) =>
        actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool InvalidRepository(AdaptiveOfflineRepositoryDefinition repository)
    {
        if (repository is null ||
            !SafeRelativePath(repository.Path, allowCurrentDirectory: true) ||
            !IsGitCommit(repository.Baseline) ||
            !Uri.TryCreate(repository.SourceUri, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme is not ("https" or "http") ||
            string.IsNullOrWhiteSpace(repository.LicenseSpdx) ||
            repository.LicenseSpdx.Length > 100 ||
            repository.LicenseSpdx.Any(character =>
                !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '+')))
        {
            return true;
        }
        return !repository.RedistributionAllowed;
    }

    private static bool InvalidProvider(AdaptiveOfflineProviderDefinition provider)
    {
        if (provider is null || !Enum.IsDefined(provider.Kind) ||
            provider.Parameters is null || provider.Parameters.Count > 10)
        {
            return true;
        }

        var supported = provider.Kind == AdaptiveOfflineProvider.Local
            ? new[] { "baseUrl", "contextWindowTokens" }
            : new[] { "baseUrl", "contextWindowTokens", "maxOutputTokens", "temperature" };
        return provider.Parameters.Any(parameter =>
            string.IsNullOrWhiteSpace(parameter.Key) || parameter.Key.Length > 100 ||
            parameter.Value is null || parameter.Value.Length > 1000 ||
            !supported.Contains(parameter.Key, StringComparer.Ordinal) ||
            InvalidParameterValue(parameter));
    }

    private static bool InvalidProtocol(AdaptiveOfflineProtocol protocol)
    {
        if (protocol is null ||
            protocol.Design != AdaptiveOfflineSchema.Design ||
            protocol.HypothesisId != AdaptiveOfflineSchema.HypothesisId ||
            string.IsNullOrWhiteSpace(protocol.Hypothesis) || protocol.Hypothesis.Length > 1000 ||
            protocol.PrimaryMetric != AdaptiveOfflineSchema.PrimaryMetric ||
            protocol.MinimumDistinctTasks is < 50 or > 500 ||
            protocol.ConfidenceLevel != 0.95 ||
            !Enum.IsDefined(protocol.ZeroReferencePolicy) ||
            protocol.DeathCriteria is null)
        {
            return true;
        }

        var death = protocol.DeathCriteria;
        return !FiniteRange(death.MinimumRelativeImprovement, -1, 10) ||
            !FiniteRange(death.MinimumAbsoluteImprovement, 0, double.MaxValue) ||
            !FiniteRange(death.MaximumCandidateFailureRate, 0, 1) ||
            !FiniteRange(death.MaximumCandidateScopeViolationRate, 0, 1) ||
            !FiniteRange(death.MaximumCandidateSecurityViolationRate, 0, 1) ||
            !FiniteRange(death.MaximumMedianLatencyRegressionRate, 0, 10) ||
            protocol.ZeroReferencePolicy == ZeroReferencePolicy.Adjust &&
            death.MinimumAbsoluteImprovement != 0;
    }

    private static bool InvalidTask(
        AdaptiveOfflineTaskDefinition task,
        DateTime preregisteredAtUtc) =>
        task is null || !ValidId(task.Id) || !SafeRelativePath(task.ContractPath) ||
        !Enum.IsDefined(task.ExpectedDecision) ||
        task.Seed is < 0 or > int.MaxValue - 20 ||
        !IsUtc(task.HistoryCutoffUtc) ||
        task.HistoryCutoffUtc > preregisteredAtUtc ||
        task.RecommendationEvidenceId == Guid.Empty ||
        !IsSha256(task.RecommendationEvidenceHash);

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
                    out var temperature) || !double.IsFinite(temperature) || temperature is < 0 or > 2,
            _ => true
        };

    private static bool FiniteRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool IsUtc(DateTime value) =>
        value != default && value.Kind == DateTimeKind.Utc;

    private static bool IsGitCommit(string value) =>
        value is not null && value.Length is 40 or 64 &&
        value.All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

    private static bool IsSha256(string value) =>
        value is not null && value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

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
}

public static class AdaptiveOfflineDatasetFingerprint
{
    public static string Create(AdaptiveOfflineDatasetManifest manifest, string baselineCommit)
    {
        AdaptiveOfflineDatasetContract.Validate(manifest);
        if (!AdaptiveOfflineDatasetContract.BaselineMatches(
                manifest.Repository.Baseline,
                baselineCommit))
        {
            throw new InvalidOperationException(
                "Adaptive offline dataset baseline does not match the resolved repository commit.");
        }

        var json = JsonSerializer.Serialize(
            manifest,
            AdaptiveOfflineDatasetLoader.SerializerOptions);
        return Hash(json + "\nresolved-baseline:" + baselineCommit.ToLowerInvariant());
    }

    public static string PairKey(
        string datasetHash,
        string taskId,
        int repetition) => Hash($"{datasetHash}\n{taskId}\n{repetition}")[7..];

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
