using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.EvidenceGraph;

public sealed class EvidenceGraphService
{
    private readonly IEvidenceGraphSource _source;

    public EvidenceGraphService(IEvidenceGraphSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public Task<EvidenceGraphQueryResult> ListAsync(
        EvidenceGraphQuery query,
        EvidenceReadScope scope,
        CancellationToken cancellationToken)
    {
        Validate(query, scope);
        return _source.QueryEvidenceGraphsAsync(query, Normalize(scope), cancellationToken);
    }

    public Task<Domain.Models.EvidenceGraph?> ShowAsync(
        Guid evidenceId,
        EvidenceReadScope scope,
        CancellationToken cancellationToken)
    {
        if (evidenceId == Guid.Empty)
            throw new ArgumentException("A non-empty evidence ID is required.", nameof(evidenceId));
        ValidateScope(scope);
        return _source.LoadEvidenceGraphAsync(evidenceId, Normalize(scope), cancellationToken);
    }

    public Task<Domain.Models.EvidenceGraph?> TraceAsync(
        Guid evidenceId,
        EvidenceReadScope scope,
        CancellationToken cancellationToken) =>
        ShowAsync(evidenceId, scope, cancellationToken);

    private static void Validate(EvidenceGraphQuery query, EvidenceReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(scope);
        if (query.Limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query), "Evidence query limit must be 1-500.");
        if (query.RunId == Guid.Empty || query.CandidateId == Guid.Empty ||
            query.PromotionId == Guid.Empty)
        {
            throw new ArgumentException("Evidence query GUID filters must be non-empty.", nameof(query));
        }
    }

    private static void ValidateScope(EvidenceReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.RepositoryPath))
            throw new UnauthorizedAccessException("Evidence read repository scope is required.");
        if (string.IsNullOrWhiteSpace(scope.Principal))
            throw new UnauthorizedAccessException("Evidence read principal is required.");
        if (!Directory.Exists(scope.RepositoryPath))
            throw new DirectoryNotFoundException(
                $"Authorized repository scope not found: {Path.GetFullPath(scope.RepositoryPath)}");
    }

    private static EvidenceReadScope Normalize(EvidenceReadScope scope) => new()
    {
        RepositoryPath = Path.GetFullPath(scope.RepositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Principal = scope.Principal.Trim()
    };
}

public static class EvidenceGraphFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(object value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    public static string ToDot(Domain.Models.EvidenceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var output = new StringBuilder();
        output.AppendLine("digraph EvidenceGraph {");
        output.AppendLine("  rankdir=LR;");
        output.AppendLine("  graph [label=\"AECS Evidence Graph\", labelloc=t];");
        output.AppendLine("  node [shape=box, style=rounded];");
        foreach (var node in graph.Nodes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var color = node.Status switch
            {
                EvidenceGraphDataStatus.Missing => "goldenrod",
                EvidenceGraphDataStatus.Invalid => "red",
                _ => "black"
            };
            output.Append("  \"").Append(Escape(node.Id)).Append("\" [label=\"")
                .Append(Escape($"{node.Kind}\\n{node.Label}"))
                .Append("\", color=\"").Append(color).Append("\", tooltip=\"")
                .Append(Escape($"origin={node.Origin}; authority={node.Authority}"))
                .AppendLine("\"];");
        }

        foreach (var edge in graph.Edges.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            output.Append("  \"").Append(Escape(edge.From)).Append("\" -> \"")
                .Append(Escape(edge.To)).Append("\" [label=\"")
                .Append(Escape(edge.Kind)).AppendLine("\"];");
        }

        output.AppendLine("}");
        return output.ToString();
    }

    public static string ToListText(EvidenceGraphQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = new StringBuilder();
        foreach (var item in result.Items)
        {
            output.Append(item.EvidenceId.ToString("N")).Append(' ')
                .Append(item.Decision).Append(' ')
                .Append(item.TaskId).Append(" baseline=")
                .Append(item.BaselineCommit).Append(" candidate=")
                .Append(item.CandidateId.ToString("N")).AppendLine();
        }
        if (result.Items.Count == 0)
            output.AppendLine("No authorized evidence matched the query.");
        foreach (var diagnostic in result.Diagnostics)
            output.Append("WARNING: ").AppendLine(diagnostic);
        return output.ToString();
    }

    public static string ToShowText(Domain.Models.EvidenceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var summary = graph.Summary;
        var output = new StringBuilder()
            .Append("Evidence: ").AppendLine(summary.EvidenceId.ToString("N"))
            .Append("Repository: ").AppendLine(graph.RepositoryPath)
            .Append("Task: ").AppendLine(summary.TaskId)
            .Append("Run: ").AppendLine(summary.RunId.ToString("N"))
            .Append("Candidate: ").AppendLine(summary.CandidateId.ToString("N"))
            .Append("Baseline: ").AppendLine(summary.BaselineCommit)
            .Append("Decision: ").AppendLine(summary.Decision.ToString())
            .Append("Diff hash: ").AppendLine(summary.DiffHash)
            .Append("Evidence hash: ").AppendLine(summary.EvidenceHash)
            .Append("Nodes/edges: ").Append(graph.Nodes.Count).Append('/')
            .AppendLine(graph.Edges.Count.ToString());
        foreach (var diagnostic in graph.Diagnostics)
            output.Append("WARNING: ").AppendLine(diagnostic);
        return output.ToString();
    }

    public static string ToTraceText(Domain.Models.EvidenceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var nodes = graph.Nodes.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var output = new StringBuilder();
        foreach (var edge in graph.Edges.OrderBy(item => item.Timestamp).ThenBy(item => item.Id))
        {
            output.Append(nodes[edge.From].Kind).Append(':').Append(nodes[edge.From].Label)
                .Append(" --").Append(edge.Kind).Append("--> ")
                .Append(nodes[edge.To].Kind).Append(':').Append(nodes[edge.To].Label)
                .Append(" [").Append(edge.Authority).AppendLine("]");
        }
        foreach (var diagnostic in graph.Diagnostics)
            output.Append("WARNING: ").AppendLine(diagnostic);
        return output.ToString();
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
