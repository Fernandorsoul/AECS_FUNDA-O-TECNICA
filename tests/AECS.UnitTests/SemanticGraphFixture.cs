using AECS.Application.SemanticLinter;
using AECS.Domain.Models;

namespace AECS.UnitTests;

internal static class SemanticGraphFixture
{
    internal const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static SemanticAnalysisInput Input(
        IEnumerable<CSharpSymbolGraphNode> baselineNodes,
        IEnumerable<CSharpSymbolGraphNode> candidateNodes,
        IEnumerable<CSharpSymbolGraphEdge>? baselineEdges = null,
        IEnumerable<CSharpSymbolGraphEdge>? candidateEdges = null,
        IEnumerable<string>? changedFiles = null,
        IEnumerable<RepositorySnapshotProject>? projects = null) =>
        SemanticAnalysisInput.From(Context(
            baselineNodes,
            candidateNodes,
            baselineEdges,
            candidateEdges,
            changedFiles,
            projects));

    internal static VerificationContext Context(
        IEnumerable<CSharpSymbolGraphNode> baselineNodes,
        IEnumerable<CSharpSymbolGraphNode> candidateNodes,
        IEnumerable<CSharpSymbolGraphEdge>? baselineEdges = null,
        IEnumerable<CSharpSymbolGraphEdge>? candidateEdges = null,
        IEnumerable<string>? changedFiles = null,
        IEnumerable<RepositorySnapshotProject>? projects = null)
    {
        var baselineNodeList = baselineNodes.ToList();
        var candidateNodeList = candidateNodes.ToList();
        var changed = (changedFiles ?? ["src/App/Changed.cs"]).ToList();
        var projectList = (projects ?? [Project("src/App/App.csproj")]).ToList();
        var baselineSnapshot = Snapshot("sha256:baseline", baselineNodeList, projectList);
        var candidateSnapshot = Snapshot("sha256:candidate", candidateNodeList, projectList);
        var baselineGraph = Graph(
            "sha256:baseline-graph",
            baselineSnapshot.SnapshotHash,
            baselineNodeList,
            baselineEdges);
        var candidateGraph = Graph(
            "sha256:candidate-graph",
            candidateSnapshot.SnapshotHash,
            candidateNodeList,
            candidateEdges);
        return new VerificationContext
        {
            CandidateChangeSet = new CandidateChangeSet
            {
                BaselineCommit = Commit,
                ModifiedFiles = changed,
                Diff = "semantic fixture",
                DiffHash = "sha256:fixture"
            },
            BaselineRepositorySnapshot = baselineSnapshot,
            CandidateRepositorySnapshot = candidateSnapshot,
            BaselineCSharpSymbolGraph = baselineGraph,
            CandidateCSharpSymbolGraph = candidateGraph
        };
    }

    internal static CSharpSymbolGraphNode Node(
        string id,
        string kind,
        string name,
        string display,
        string project = "src/App/App.csproj",
        string? file = "src/App/Changed.cs",
        string containing = "",
        string accessibility = "Public",
        string typeKind = "",
        string memberKind = "",
        string documentationId = "",
        IEnumerable<string>? modifiers = null,
        bool external = false) => new()
        {
            Id = id,
            Kind = kind,
            Name = name,
            DisplayName = display,
            DocumentationId = documentationId,
            ProjectPath = project,
            FilePaths = file is null ? [] : [file],
            ContainingNodeId = containing,
            Accessibility = accessibility,
            TypeKind = typeKind,
            MemberKind = memberKind,
            AssemblyName = Path.GetFileNameWithoutExtension(project),
            Modifiers = modifiers?.ToList() ?? [],
            IsExternal = external
        };

    internal static CSharpSymbolGraphEdge Edge(string kind, string from, string to) => new()
    {
        Id = $"{kind}:{from}:{to}",
        Kind = kind,
        FromNodeId = from,
        ToNodeId = to
    };

    internal static RepositorySnapshotProject Project(string path, bool test = false) => new()
    {
        Path = path,
        Hash = $"git:{path}",
        Language = "C#",
        IsTestProject = test,
        Frameworks = ["net8.0"]
    };

    private static RepositorySnapshot Snapshot(
        string hash,
        IEnumerable<CSharpSymbolGraphNode> nodes,
        List<RepositorySnapshotProject> projects) => new()
        {
            SnapshotHash = hash,
            BaselineCommit = Commit,
            TaskContractId = "TASK-SEMANTIC",
            Projects = projects,
            Files = nodes.SelectMany(node => node.FilePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new RepositorySnapshotFile
                {
                    Path = path,
                    Hash = $"git:{path}",
                    Language = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "C#" : ""
                })
                .ToList()
        };

    private static CSharpSymbolGraph Graph(
        string hash,
        string snapshotHash,
        IEnumerable<CSharpSymbolGraphNode> nodes,
        IEnumerable<CSharpSymbolGraphEdge>? edges) => new()
        {
            GraphHash = hash,
            RepositorySnapshotHash = snapshotHash,
            BaselineCommit = Commit,
            LoadSucceeded = true,
            Nodes = nodes.ToList(),
            Edges = edges?.ToList() ?? []
        };
}
