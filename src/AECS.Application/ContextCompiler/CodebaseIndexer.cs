using AECS.Domain.Models;

namespace AECS.Application.ContextCompiler;

public class CodeSymbol
{
    public string Id { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public List<string> BaseTypes { get; init; } = [];
    public List<string> Methods { get; init; } = [];
}

public sealed class CodeFileRelation
{
    public string FromPath { get; init; } = string.Empty;
    public string ToPath { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
}

public class CodebaseIndex
{
    public string RootPath { get; init; } = string.Empty;
    public List<string> SourceFiles { get; init; } = [];
    public List<string> TestFiles { get; init; } = [];
    public List<CodeSymbol> Symbols { get; init; } = [];
    public Dictionary<string, List<string>> Dependencies { get; init; } = new();
    public Dictionary<string, List<CodeFileRelation>> FileRelations { get; init; } = new();
    public bool SemanticAuthority { get; init; }
    public string SymbolGraphHash { get; init; } = string.Empty;
    public string Source { get; init; } = "textual-file-inventory";
}

public class CodebaseIndexer
{
    private static readonly HashSet<string> SemanticFileEdgeKinds = new(
        StringComparer.Ordinal)
    {
        "constructs", "implements", "inherits", "references"
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules",
        "packages", "dist", "build", "coverage", "TestResults", "artifacts"
    };

    public CodebaseIndex Index(string rootPath, CSharpSymbolGraph? symbolGraph = null)
    {
        var resolvedRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(resolvedRoot))
            throw new DirectoryNotFoundException($"Context root not found: {resolvedRoot}");

        var csFiles = EnumerateCSharpFiles(resolvedRoot);
        var sourceFiles = csFiles.Where(path => !IsTestFile(path)).ToList();
        var testFiles = csFiles.Where(IsTestFile).ToList();
        var semanticAuthority = symbolGraph is
        {
            LoadSucceeded: true,
            SchemaVersion: CSharpSymbolGraphSchema.GraphVersion,
            StrategyVersion: CSharpSymbolGraphSchema.StrategyVersion
        } && !string.IsNullOrWhiteSpace(symbolGraph.GraphHash) &&
            string.Equals(
                symbolGraph.GraphHash,
                CSharpSymbolGraphFingerprint.Create(symbolGraph),
                StringComparison.Ordinal);

        return new CodebaseIndex
        {
            RootPath = resolvedRoot,
            SourceFiles = sourceFiles,
            TestFiles = testFiles,
            Symbols = semanticAuthority ? ProjectSymbols(symbolGraph!) : [],
            Dependencies = semanticAuthority ? ProjectDependencies(symbolGraph!) : new(),
            FileRelations = semanticAuthority ? ProjectFileRelations(symbolGraph!) : new(),
            SemanticAuthority = semanticAuthority,
            SymbolGraphHash = semanticAuthority ? symbolGraph!.GraphHash : string.Empty,
            Source = semanticAuthority ? "roslyn-symbol-graph" : "textual-file-inventory"
        };
    }

    private static List<string> EnumerateCSharpFiles(string root)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden |
                FileAttributes.System |
                FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*.cs", enumerationOptions)
            .Where(file => !HasExcludedDirectory(root, file))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    private static List<CodeSymbol> ProjectSymbols(CSharpSymbolGraph graph)
    {
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var contains = graph.Edges.Where(edge => edge.Kind == "contains")
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.ToNodeId).ToList(),
                StringComparer.Ordinal);
        var inherits = graph.Edges.Where(edge => edge.Kind == "inherits")
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.ToNodeId).ToList(),
                StringComparer.Ordinal);
        return graph.Nodes.Where(node => node.Kind == "type" && !node.IsExternal &&
                node.FilePaths.Count > 0)
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .SelectMany(node => node.FilePaths
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new CodeSymbol
                {
                    Id = node.Id,
                    Hash = node.Hash,
                    Name = node.Name,
                    DisplayName = node.DisplayName,
                    Kind = node.TypeKind.ToLowerInvariant(),
                    FilePath = path,
                    ProjectPath = node.ProjectPath,
                    Namespace = nodes.GetValueOrDefault(node.ContainingNodeId) is
                    { Kind: "namespace" } containingNamespace
                    ? containingNamespace.DisplayName
                    : string.Empty,
                    BaseTypes = inherits.GetValueOrDefault(node.Id, [])
                    .Select(id => nodes.GetValueOrDefault(id)?.DisplayName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList(),
                    Methods = contains.GetValueOrDefault(node.Id, [])
                    .Select(id => nodes.GetValueOrDefault(id))
                    .Where(member => member is { Kind: "member", MemberKind: "Method" })
                    .Select(member => member!.Name)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList()
                }))
            .ToList();
    }

    private static Dictionary<string, List<string>> ProjectDependencies(CSharpSymbolGraph graph)
    {
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges.Where(edge => edge.Kind == "references"))
        {
            if (!nodes.TryGetValue(edge.FromNodeId, out var source) ||
                !nodes.TryGetValue(edge.ToNodeId, out var target))
            {
                continue;
            }
            foreach (var path in source.FilePaths)
            {
                if (!result.TryGetValue(path, out var dependencies))
                {
                    dependencies = new HashSet<string>(StringComparer.Ordinal);
                    result[path] = dependencies;
                }
                dependencies.Add(target.DisplayName);
            }
        }
        return result.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => item.Value.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<CodeFileRelation>> ProjectFileRelations(
        CSharpSymbolGraph graph)
    {
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var relations = new Dictionary<string, Dictionary<string, CodeFileRelation>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var edge in graph.Edges
                     .Where(edge => SemanticFileEdgeKinds.Contains(edge.Kind))
                     .OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            if (!nodes.TryGetValue(edge.FromNodeId, out var source) ||
                !nodes.TryGetValue(edge.ToNodeId, out var target) ||
                source.IsExternal || target.IsExternal)
            {
                continue;
            }

            foreach (var sourcePath in source.FilePaths.OrderBy(path => path, StringComparer.Ordinal))
                foreach (var targetPath in target.FilePaths.OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    AddFileRelation(relations, sourcePath, targetPath, edge.Kind, target.DisplayName);
                    AddFileRelation(
                        relations,
                        targetPath,
                        sourcePath,
                        $"referenced-by:{edge.Kind}",
                        source.DisplayName);
                }
        }

        foreach (var node in graph.Nodes.Where(node =>
                     !node.IsExternal &&
                     node.Kind == "type" &&
                     node.Modifiers.Contains("partial", StringComparer.Ordinal) &&
                     node.FilePaths.Count > 1))
        {
            foreach (var sourcePath in node.FilePaths.OrderBy(path => path, StringComparer.Ordinal))
                foreach (var targetPath in node.FilePaths.OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                        AddFileRelation(relations, sourcePath, targetPath, "partial", node.DisplayName);
                }
        }

        return relations.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => item.Value.Values
                    .OrderBy(relation => relation.ToPath, StringComparer.Ordinal)
                    .ThenBy(relation => relation.Kind, StringComparer.Ordinal)
                    .ThenBy(relation => relation.Symbol, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AddFileRelation(
        IDictionary<string, Dictionary<string, CodeFileRelation>> relations,
        string fromPath,
        string toPath,
        string kind,
        string symbol)
    {
        if (!relations.TryGetValue(fromPath, out var outgoing))
        {
            outgoing = new Dictionary<string, CodeFileRelation>(StringComparer.Ordinal);
            relations[fromPath] = outgoing;
        }
        var key = $"{toPath}|{kind}|{symbol}";
        outgoing.TryAdd(key, new CodeFileRelation
        {
            FromPath = fromPath,
            ToPath = toPath,
            Kind = kind,
            Symbol = symbol
        });
    }

    private static bool IsTestFile(string path)
    {
        var segments = path.Split('/');
        return segments.Any(segment =>
                segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains("Test", StringComparison.OrdinalIgnoreCase)) ||
            Path.GetFileNameWithoutExtension(path)
                .EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExcludedDirectory(string rootPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Take(Math.Max(segments.Length - 1, 0))
            .Any(ExcludedDirectoryNames.Contains);
    }
}
