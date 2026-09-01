using AECS.Domain.Models;

namespace AECS.Application.SemanticLinter;

public sealed class SemanticAnalysisInput
{
    private static readonly HashSet<string> PublicAccessibilities = new(
        ["Public", "Protected", "ProtectedOrInternal"],
        StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, CSharpSymbolGraphNode> _baselineNodes;
    private readonly IReadOnlyDictionary<string, CSharpSymbolGraphNode> _candidateNodes;

    private SemanticAnalysisInput(
        RepositorySnapshot baselineSnapshot,
        RepositorySnapshot candidateSnapshot,
        CSharpSymbolGraph baselineGraph,
        CSharpSymbolGraph candidateGraph,
        CandidateChangeSet candidate)
    {
        BaselineSnapshot = baselineSnapshot;
        CandidateSnapshot = candidateSnapshot;
        BaselineGraph = baselineGraph;
        CandidateGraph = candidateGraph;
        Candidate = candidate;
        ChangedFiles = candidate.ChangedFiles
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _baselineNodes = baselineGraph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        _candidateNodes = candidateGraph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
    }

    public RepositorySnapshot BaselineSnapshot { get; }
    public RepositorySnapshot CandidateSnapshot { get; }
    public CSharpSymbolGraph BaselineGraph { get; }
    public CSharpSymbolGraph CandidateGraph { get; }
    public CandidateChangeSet Candidate { get; }
    public IReadOnlySet<string> ChangedFiles { get; }

    public static SemanticAnalysisInput From(VerificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.IsNullOrWhiteSpace(context.SemanticAnalysisError))
            throw new InvalidOperationException(context.SemanticAnalysisError);
        var baselineSnapshot = context.BaselineRepositorySnapshot ??
            throw new InvalidOperationException("Baseline repository snapshot is unavailable.");
        var candidateSnapshot = context.CandidateRepositorySnapshot ??
            throw new InvalidOperationException("Candidate repository snapshot is unavailable.");
        var baselineGraph = context.BaselineCSharpSymbolGraph ??
            throw new InvalidOperationException("Baseline C# symbol graph is unavailable.");
        var candidateGraph = context.CandidateCSharpSymbolGraph ??
            throw new InvalidOperationException("Candidate C# symbol graph is unavailable.");
        var requiresCSharpAnalysis = context.CandidateChangeSet.ChangedFiles.Any(path =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase));
        if (requiresCSharpAnalysis && !baselineGraph.LoadSucceeded)
            throw new InvalidOperationException("Baseline C# symbol graph did not load successfully.");
        if (requiresCSharpAnalysis && !candidateGraph.LoadSucceeded)
            throw new InvalidOperationException("Candidate C# symbol graph did not load successfully.");
        if (!string.Equals(
                baselineGraph.RepositorySnapshotHash,
                baselineSnapshot.SnapshotHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidateGraph.RepositorySnapshotHash,
                candidateSnapshot.SnapshotHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "C# symbol graph does not match its authenticated repository snapshot.");
        }
        if (!string.Equals(
                context.CandidateChangeSet.BaselineCommit,
                baselineSnapshot.BaselineCommit,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                candidateSnapshot.BaselineCommit,
                baselineSnapshot.BaselineCommit,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Semantic snapshots do not share the candidate baseline commit.");
        }
        return new SemanticAnalysisInput(
            baselineSnapshot,
            candidateSnapshot,
            baselineGraph,
            candidateGraph,
            context.CandidateChangeSet);
    }

    public IEnumerable<CSharpSymbolGraphNode> ImpactedBaselineNodes() =>
        BaselineGraph.Nodes.Where(node => IsImpacted(node, BaselineGraph));

    public IEnumerable<CSharpSymbolGraphNode> ImpactedCandidateNodes() =>
        CandidateGraph.Nodes.Where(node => IsImpacted(node, CandidateGraph));

    public bool IsImpacted(CSharpSymbolGraphNode node, CSharpSymbolGraph graph)
    {
        if (node.FilePaths.Any(ChangedFiles.Contains))
            return true;
        if (node.Kind == "file" && node.FilePaths.Any(ChangedFiles.Contains))
            return true;
        if (node.Kind == "project" && ChangedFiles.Contains(node.ProjectPath))
            return true;
        return node.Kind == "project" && graph.Nodes.Any(candidate =>
            candidate.ProjectPath.Equals(node.ProjectPath, StringComparison.OrdinalIgnoreCase) &&
            candidate.FilePaths.Any(ChangedFiles.Contains));
    }

    public CSharpSymbolGraphNode? BaselineNode(string id) =>
        _baselineNodes.GetValueOrDefault(id);

    public CSharpSymbolGraphNode? CandidateNode(string id) =>
        _candidateNodes.GetValueOrDefault(id);

    public CSharpSymbolGraphNode? Node(CSharpSymbolGraph graph, string id) =>
        ReferenceEquals(graph, BaselineGraph) ? BaselineNode(id) : CandidateNode(id);

    public CSharpSymbolGraphNode? ContainingType(
        CSharpSymbolGraphNode node,
        CSharpSymbolGraph graph)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(current.ContainingNodeId) &&
               visited.Add(current.ContainingNodeId))
        {
            var parent = Node(graph, current.ContainingNodeId);
            if (parent is null)
                return null;
            if (parent.Kind == "type")
                return parent;
            current = parent;
        }
        return null;
    }

    public string NamespaceOf(CSharpSymbolGraphNode node, CSharpSymbolGraph graph)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(current.ContainingNodeId) &&
               visited.Add(current.ContainingNodeId))
        {
            var parent = Node(graph, current.ContainingNodeId);
            if (parent is null)
                break;
            if (parent.Kind == "namespace")
                return parent.DisplayName;
            current = parent;
        }
        return string.Empty;
    }

    public bool IsPublicApi(CSharpSymbolGraphNode node, CSharpSymbolGraph graph)
    {
        if (node.Kind is not ("type" or "member") ||
            !PublicAccessibilities.Contains(node.Accessibility))
        {
            return false;
        }
        var current = node;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(current.ContainingNodeId) &&
               visited.Add(current.ContainingNodeId))
        {
            var parent = Node(graph, current.ContainingNodeId);
            if (parent is null)
                break;
            if (parent.Kind == "type" && !PublicAccessibilities.Contains(parent.Accessibility))
                return false;
            current = parent;
        }
        return true;
    }

    public bool IsTestProject(string projectPath, RepositorySnapshot snapshot) =>
        snapshot.Projects.Any(project =>
            project.Path.Equals(projectPath, StringComparison.OrdinalIgnoreCase) &&
            project.IsTestProject);

    public bool IsContainedBy(
        CSharpSymbolGraphNode node,
        CSharpSymbolGraphNode ancestor,
        CSharpSymbolGraph graph)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(current.ContainingNodeId) &&
               visited.Add(current.ContainingNodeId))
        {
            if (current.ContainingNodeId == ancestor.Id)
                return true;
            var parent = Node(graph, current.ContainingNodeId);
            if (parent is null)
                return false;
            current = parent;
        }
        return false;
    }

    public static string Location(CSharpSymbolGraphNode node) =>
        node.FilePaths.OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault() ??
        node.ProjectPath;

    public static string NormalizePath(string path) => path.Replace('\\', '/').Trim();
}

public sealed class SemanticRuleFinding
{
    public string RuleId { get; init; } = string.Empty;
    public string RuleName { get; init; } = string.Empty;
    public string SymbolId { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public RuleSeverity Severity { get; init; }
    public string Baseline { get; init; } = string.Empty;
    public string Justification { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
}

public static class SemanticEvidenceFactory
{
    public static SemanticVerificationEvidence Create(
        SemanticAnalysisInput input,
        IEnumerable<SemanticRuleFinding> findings) => new()
        {
            BaselineCommit = input.BaselineSnapshot.BaselineCommit,
            BaselineSnapshotHash = input.BaselineSnapshot.SnapshotHash,
            CandidateSnapshotHash = input.CandidateSnapshot.SnapshotHash,
            BaselineGraphHash = input.BaselineGraph.GraphHash,
            CandidateGraphHash = input.CandidateGraph.GraphHash,
            ImpactedFiles = input.ChangedFiles.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            Findings = findings.Select(finding => new SemanticFindingEvidence
                {
                    RuleId = finding.RuleId,
                    SymbolId = finding.SymbolId,
                    Symbol = finding.Symbol,
                    FilePath = finding.FilePath,
                    Severity = finding.Severity.ToString(),
                    Baseline = finding.Baseline,
                    Justification = finding.Justification
                })
                .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.FilePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.SymbolId, StringComparer.Ordinal)
                .ToList()
        };
}
