using System.Text.RegularExpressions;

namespace AECS.Application.ContextCompiler;

public class CodeSymbol
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty; // class, interface, enum, struct
    public string FilePath { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public List<string> BaseTypes { get; init; } = [];
    public List<string> Methods { get; init; } = [];
}

public class CodebaseIndex
{
    public string RootPath { get; init; } = string.Empty;
    public List<string> SourceFiles { get; init; } = [];
    public List<string> TestFiles { get; init; } = [];
    public List<CodeSymbol> Symbols { get; init; } = [];
    public Dictionary<string, List<string>> Dependencies { get; init; } = new();
}

public class CodebaseIndexer
{
    private static readonly Regex ClassRegex = new(
        @"(?:public|internal|private|protected)?\s*(?:static\s+)?(?:partial\s+)?(?:class|interface|enum|struct|record)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceRegex = new(
        @"namespace\s+([\w.]+)",
        RegexOptions.Compiled);

    private static readonly Regex MethodRegex = new(
        @"(?:public|internal|private|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?,\s]+\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex UsingRegex = new(
        @"using\s+([\w.]+);",
        RegexOptions.Compiled);

    public CodebaseIndex Index(string rootPath)
    {
        var sourceFiles = new List<string>();
        var testFiles = new List<string>();
        var symbols = new List<CodeSymbol>();
        var dependencies = new Dictionary<string, List<string>>();

        var csFiles = Directory.GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")))
            .ToList();

        foreach (var file in csFiles)
        {
            var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');

            if (IsTestFile(relativePath))
                testFiles.Add(relativePath);
            else
                sourceFiles.Add(relativePath);

            var content = File.ReadAllText(file);
            var fileSymbols = ExtractSymbols(content, relativePath);
            symbols.AddRange(fileSymbols);

            var usings = ExtractUsings(content);
            dependencies[relativePath] = usings;
        }

        return new CodebaseIndex
        {
            RootPath = rootPath,
            SourceFiles = sourceFiles,
            TestFiles = testFiles,
            Symbols = symbols,
            Dependencies = dependencies
        };
    }

    private static List<CodeSymbol> ExtractSymbols(string content, string filePath)
    {
        var symbols = new List<CodeSymbol>();
        var namespaceMatch = NamespaceRegex.Match(content);
        var ns = namespaceMatch.Success ? namespaceMatch.Groups[1].Value : "";

        foreach (Match match in ClassRegex.Matches(content))
        {
            var kind = match.Value.Contains("interface") ? "interface"
                : match.Value.Contains("enum") ? "enum"
                : match.Value.Contains("struct") ? "struct"
                : match.Value.Contains("record") ? "record"
                : "class";

            symbols.Add(new CodeSymbol
            {
                Name = match.Groups[1].Value,
                Kind = kind,
                FilePath = filePath,
                Namespace = ns
            });
        }

        return symbols;
    }

    private static List<string> ExtractUsings(string content)
    {
        return UsingRegex.Matches(content)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private static bool IsTestFile(string path)
    {
        return path.Contains("Test", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith(".cs") || path.EndsWith(".csproj"));
    }
}
