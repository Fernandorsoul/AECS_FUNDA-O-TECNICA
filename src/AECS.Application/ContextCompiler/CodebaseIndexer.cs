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
    private static readonly HashSet<string> ExcludedDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules",
        "packages", "dist", "build", "coverage", "TestResults", "artifacts"
    };

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
        var resolvedRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(resolvedRoot))
            throw new DirectoryNotFoundException($"Context root not found: {resolvedRoot}");

        var sourceFiles = new List<string>();
        var testFiles = new List<string>();
        var symbols = new List<CodeSymbol>();
        var dependencies = new Dictionary<string, List<string>>();

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden |
                FileAttributes.System |
                FileAttributes.ReparsePoint
        };
        var csFiles = Directory.EnumerateFiles(resolvedRoot, "*.cs", enumerationOptions)
            .Where(file => !HasExcludedDirectory(resolvedRoot, file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in csFiles)
        {
            var relativePath = Path.GetRelativePath(resolvedRoot, file).Replace('\\', '/');

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
            RootPath = resolvedRoot,
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
        var methods = MethodRegex.Matches(content)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

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
                Namespace = ns,
                Methods = methods
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
        var segments = path.Replace('\\', '/').Split('/');
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
