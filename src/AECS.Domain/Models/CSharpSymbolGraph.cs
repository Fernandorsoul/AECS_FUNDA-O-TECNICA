using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Domain.Models;

public static class CSharpSymbolGraphSchema
{
    public const string GraphVersion = "aecs.csharp-symbol-graph/v1";
    public const string LegacyStrategyVersion = "roslyn-msbuild-symbol-graph/v1";
    public const string StrategyVersion = "roslyn-msbuild-symbol-graph/v2";

    public static bool IsSupportedStrategyVersion(string value) =>
        value is LegacyStrategyVersion or StrategyVersion;
}

public sealed class CSharpSymbolGraphLimits
{
    public const int DefaultMaxDurationSeconds = 90;
    public const long DefaultMaxEstimatedMemoryBytes = 256L * 1024 * 1024;
    public const int DefaultMaxProjects = 128;
    public const int DefaultMaxDocuments = 20_000;
    public const int DefaultMaxNodes = 200_000;
    public const int DefaultMaxEdges = 1_000_000;
    public const int DefaultMaxDiagnostics = 2_000;

    public int MaxDurationSeconds { get; init; } = DefaultMaxDurationSeconds;
    public long MaxEstimatedMemoryBytes { get; init; } = DefaultMaxEstimatedMemoryBytes;
    public int MaxProjects { get; init; } = DefaultMaxProjects;
    public int MaxDocuments { get; init; } = DefaultMaxDocuments;
    public int MaxNodes { get; init; } = DefaultMaxNodes;
    public int MaxEdges { get; init; } = DefaultMaxEdges;
    public int MaxDiagnostics { get; init; } = DefaultMaxDiagnostics;
}

public sealed class CSharpSymbolGraph
{
    public string SchemaVersion { get; init; } = CSharpSymbolGraphSchema.GraphVersion;
    public string StrategyVersion { get; init; } = CSharpSymbolGraphSchema.StrategyVersion;
    public string GraphHash { get; init; } = string.Empty;
    public string RepositorySnapshotHash { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string CompilerVersion { get; init; } = string.Empty;
    public string MsBuildVersion { get; init; } = string.Empty;
    public string SdkVersion { get; init; } = string.Empty;
    public bool LoadSucceeded { get; init; }
    public CSharpSymbolGraphLimits Limits { get; init; } = new();
    public List<CSharpSymbolGraphOption> GlobalProperties { get; init; } = [];
    public List<CSharpSymbolGraphProject> Projects { get; init; } = [];
    public List<CSharpSymbolGraphNode> Nodes { get; init; } = [];
    public List<CSharpSymbolGraphEdge> Edges { get; init; } = [];
    public List<CSharpSymbolGraphDiagnostic> Diagnostics { get; init; } = [];
}

public sealed class CSharpSymbolGraphOption
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class CSharpSymbolGraphProject
{
    public string Id { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string AssemblyName { get; init; } = string.Empty;
    public List<string> TargetFrameworks { get; init; } = [];
    public string LanguageVersion { get; init; } = string.Empty;
    public string DocumentationMode { get; init; } = string.Empty;
    public List<string> PreprocessorSymbols { get; init; } = [];
    public string OutputKind { get; init; } = string.Empty;
    public string NullableContext { get; init; } = string.Empty;
    public string OptimizationLevel { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public bool AllowUnsafe { get; init; }
    public bool Deterministic { get; init; }
    public List<string> ProjectReferences { get; init; } = [];
}

public sealed class CSharpSymbolGraphNode
{
    public string Id { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string DocumentationId { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public List<string> FilePaths { get; init; } = [];
    public string SourceHash { get; init; } = string.Empty;
    public string ContainingNodeId { get; init; } = string.Empty;
    public string Accessibility { get; init; } = string.Empty;
    public List<string> Modifiers { get; init; } = [];
    public int Arity { get; init; }
    public string TypeKind { get; init; } = string.Empty;
    public string MemberKind { get; init; } = string.Empty;
    public string AssemblyName { get; init; } = string.Empty;
    public bool IsExternal { get; init; }
}

public sealed class CSharpSymbolGraphEdge
{
    public string Id { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string FromNodeId { get; init; } = string.Empty;
    public string ToNodeId { get; init; } = string.Empty;
}

public sealed class CSharpSymbolGraphDiagnostic
{
    public string Id { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public int Column { get; init; }
}

public static class CSharpSymbolGraphFingerprint
{
    public static string StableNodeId(string identity) => "CSN-" + Digest(identity)[7..39];

    public static string StableNodeId(CSharpSymbolGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var identity = node.Kind switch
        {
            "project" => $"project|{node.ProjectPath}",
            "file" when node.FilePaths.Count == 1 =>
                $"file|{node.ProjectPath}|{node.FilePaths[0]}",
            "namespace" or "type" or "member" =>
                $"{node.Kind}|{node.ProjectPath}|{node.AssemblyName}|" +
                (string.IsNullOrWhiteSpace(node.DocumentationId)
                    ? node.DisplayName
                    : node.DocumentationId),
            _ => string.Empty
        };
        return string.IsNullOrEmpty(identity) ? string.Empty : StableNodeId(identity);
    }

    public static string StableEdgeId(string kind, string fromNodeId, string toNodeId) =>
        "CSE-" + Digest($"{kind}\n{fromNodeId}\n{toNodeId}")[7..39];

    public static string StableProjectId(string path) =>
        "CSP-" + Digest(path)[7..39];

    public static string StableDiagnosticId(string identity) =>
        "CSD-" + Digest(identity)[7..39];

    public static string StableDiagnosticId(CSharpSymbolGraphDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return StableDiagnosticId(
            $"{diagnostic.Source}|{diagnostic.Severity}|{diagnostic.Code}|" +
            $"{diagnostic.Message}|{diagnostic.ProjectPath}|{diagnostic.FilePath}|" +
            $"{diagnostic.Line}|{diagnostic.Column}");
    }

    public static string Create(CSharpSymbolGraph graph) => Digest(JsonSerializer.Serialize(
        new
        {
            graph.SchemaVersion,
            graph.StrategyVersion,
            graph.RepositorySnapshotHash,
            graph.CompilerVersion,
            graph.MsBuildVersion,
            graph.SdkVersion,
            graph.LoadSucceeded,
            graph.Limits,
            graph.GlobalProperties,
            graph.Projects,
            graph.Nodes,
            graph.Edges,
            graph.Diagnostics
        }));

    public static string CreateProject(CSharpSymbolGraphProject project) => Digest(
        JsonSerializer.Serialize(new
        {
            project.Id,
            project.Path,
            project.Name,
            project.AssemblyName,
            project.TargetFrameworks,
            project.LanguageVersion,
            project.DocumentationMode,
            project.PreprocessorSymbols,
            project.OutputKind,
            project.NullableContext,
            project.OptimizationLevel,
            project.Platform,
            project.AllowUnsafe,
            project.Deterministic,
            project.ProjectReferences
        }));

    public static string CreateNode(CSharpSymbolGraphNode node) => Digest(
        JsonSerializer.Serialize(new
        {
            node.Id,
            node.Kind,
            node.Name,
            node.DisplayName,
            node.DocumentationId,
            node.ProjectPath,
            node.FilePaths,
            node.SourceHash,
            node.ContainingNodeId,
            node.Accessibility,
            node.Modifiers,
            node.Arity,
            node.TypeKind,
            node.MemberKind,
            node.AssemblyName,
            node.IsExternal
        }));

    public static string CreateEdge(CSharpSymbolGraphEdge edge) => Digest(
        JsonSerializer.Serialize(new
        {
            edge.Id,
            edge.Kind,
            edge.FromNodeId,
            edge.ToNodeId
        }));

    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
