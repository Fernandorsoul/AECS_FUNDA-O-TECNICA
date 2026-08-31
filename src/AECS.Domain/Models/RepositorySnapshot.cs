using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Domain.Models;

public static class RepositorySnapshotSchema
{
    public const string ProfileVersion = "aecs.repository-snapshot-profile/v1";
    public const string SnapshotVersion = "aecs.repository-snapshot/v1";
    public const string DiscoveryStrategy = "git-tree-manifest-inventory/v1";
}

public sealed class RepositorySnapshotProfile
{
    public string Version { get; init; } = RepositorySnapshotSchema.ProfileVersion;
    public List<string> ExcludedDirectories { get; init; } = [];
}

public sealed class RepositorySnapshot
{
    public string SchemaVersion { get; init; } = RepositorySnapshotSchema.SnapshotVersion;
    public string StrategyVersion { get; init; } = RepositorySnapshotSchema.DiscoveryStrategy;
    public string SnapshotHash { get; init; } = string.Empty;
    public string ConfigurationHash { get; init; } = string.Empty;
    public string ProfileVersion { get; init; } = RepositorySnapshotSchema.ProfileVersion;
    public string BaselineCommit { get; init; } = string.Empty;
    public string TaskContractId { get; init; } = string.Empty;
    public int ExcludedEntryCount { get; init; }
    public List<string> ExcludedDirectories { get; init; } = [];
    public List<RepositorySnapshotTool> Tools { get; init; } = [];
    public List<RepositorySnapshotFile> Files { get; init; } = [];
    public List<RepositorySnapshotSolution> Solutions { get; init; } = [];
    public List<RepositorySnapshotProject> Projects { get; init; } = [];
    public List<RepositorySnapshotLanguage> Languages { get; init; } = [];
    public List<string> Frameworks { get; init; } = [];
    public List<RepositorySnapshotManifest> Manifests { get; init; } = [];
    public List<RepositorySnapshotPackage> Packages { get; init; } = [];
    public List<RepositorySnapshotEntrypoint> Entrypoints { get; init; } = [];
    public List<RepositorySnapshotTestSuite> TestSuites { get; init; } = [];
    public List<RepositorySnapshotRelationship> Relationships { get; init; } = [];
}

public sealed class RepositorySnapshotTool
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool Available { get; init; }
    public string Runtime { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public string ImageDigest { get; init; } = string.Empty;
}

public sealed class RepositorySnapshotFile
{
    public string Path { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
}

public sealed class RepositorySnapshotSolution
{
    public string Path { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public List<string> Projects { get; init; } = [];
}

public sealed class RepositorySnapshotProject
{
    public string Path { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Sdk { get; init; } = string.Empty;
    public string OutputType { get; init; } = string.Empty;
    public bool IsTestProject { get; init; }
    public List<string> Frameworks { get; init; } = [];
    public List<string> ProjectReferences { get; init; } = [];
    public List<string> PackageReferences { get; init; } = [];
}

public sealed class RepositorySnapshotLanguage
{
    public string Name { get; init; } = string.Empty;
    public int FileCount { get; init; }
}

public sealed class RepositorySnapshotManifest
{
    public string Path { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
}

public sealed class RepositorySnapshotPackage
{
    public string Ecosystem { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
}

public sealed class RepositorySnapshotEntrypoint
{
    public string Path { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
}

public sealed class RepositorySnapshotTestSuite
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

public sealed class RepositorySnapshotRelationship
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
}

public sealed class RepositorySnapshotDiff
{
    public string FromSnapshotHash { get; init; } = string.Empty;
    public string ToSnapshotHash { get; init; } = string.Empty;
    public List<string> AddedFiles { get; init; } = [];
    public List<string> RemovedFiles { get; init; } = [];
    public List<string> ChangedFiles { get; init; } = [];

    public bool HasChanges =>
        AddedFiles.Count > 0 || RemovedFiles.Count > 0 || ChangedFiles.Count > 0;
}

public static class RepositorySnapshotFingerprint
{
    public static string Create(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var content = new SnapshotFingerprintContent
        {
            SchemaVersion = snapshot.SchemaVersion,
            StrategyVersion = snapshot.StrategyVersion,
            ConfigurationHash = snapshot.ConfigurationHash,
            ProfileVersion = snapshot.ProfileVersion,
            ExcludedDirectories = snapshot.ExcludedDirectories,
            Files = snapshot.Files,
            Solutions = snapshot.Solutions,
            Projects = snapshot.Projects,
            Languages = snapshot.Languages,
            Frameworks = snapshot.Frameworks,
            Manifests = snapshot.Manifests,
            Packages = snapshot.Packages,
            Entrypoints = snapshot.Entrypoints,
            TestSuites = snapshot.TestSuites,
            Relationships = snapshot.Relationships
        };
        return Hash(JsonSerializer.Serialize(content));
    }

    public static string CreateConfiguration(
        string profileVersion,
        IReadOnlyCollection<string> excludedDirectories) => Hash(JsonSerializer.Serialize(
        new SnapshotConfigurationFingerprint
        {
            ProfileVersion = profileVersion,
            ExcludedDirectories = excludedDirectories.ToList()
        }));

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class SnapshotConfigurationFingerprint
    {
        public string ProfileVersion { get; init; } = string.Empty;
        public List<string> ExcludedDirectories { get; init; } = [];
    }

    private sealed class SnapshotFingerprintContent
    {
        public string SchemaVersion { get; init; } = string.Empty;
        public string StrategyVersion { get; init; } = string.Empty;
        public string ConfigurationHash { get; init; } = string.Empty;
        public string ProfileVersion { get; init; } = string.Empty;
        public List<string> ExcludedDirectories { get; init; } = [];
        public List<RepositorySnapshotFile> Files { get; init; } = [];
        public List<RepositorySnapshotSolution> Solutions { get; init; } = [];
        public List<RepositorySnapshotProject> Projects { get; init; } = [];
        public List<RepositorySnapshotLanguage> Languages { get; init; } = [];
        public List<string> Frameworks { get; init; } = [];
        public List<RepositorySnapshotManifest> Manifests { get; init; } = [];
        public List<RepositorySnapshotPackage> Packages { get; init; } = [];
        public List<RepositorySnapshotEntrypoint> Entrypoints { get; init; } = [];
        public List<RepositorySnapshotTestSuite> TestSuites { get; init; } = [];
        public List<RepositorySnapshotRelationship> Relationships { get; init; } = [];
    }
}

public static class RepositorySnapshotComparer
{
    public static RepositorySnapshotDiff Compare(
        RepositorySnapshot from,
        RepositorySnapshot to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var fromFiles = from.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var toFiles = to.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        return new RepositorySnapshotDiff
        {
            FromSnapshotHash = from.SnapshotHash,
            ToSnapshotHash = to.SnapshotHash,
            AddedFiles = toFiles.Keys.Except(fromFiles.Keys, StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList(),
            RemovedFiles = fromFiles.Keys.Except(toFiles.Keys, StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList(),
            ChangedFiles = fromFiles.Keys.Intersect(toFiles.Keys, StringComparer.Ordinal)
                .Where(path => !string.Equals(
                    fromFiles[path].Hash,
                    toFiles[path].Hash,
                    StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
        };
    }
}
