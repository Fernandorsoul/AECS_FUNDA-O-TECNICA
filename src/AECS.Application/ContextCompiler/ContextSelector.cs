using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace AECS.Application.ContextCompiler;

public class ContextPackage
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public List<string> SelectedFiles { get; init; } = [];
    public List<string> SelectedSymbols { get; init; } = [];
    public List<string> RelevantTests { get; init; } = [];
    public Dictionary<string, List<string>> SymbolsByFile { get; init; } = new();
    public int EstimatedTokens { get; init; }
    public int EligibleFileCount { get; init; }
    public string Strategy { get; init; } = "default";
}

public class ContextSelector
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "and", "are", "com", "como", "das", "dos", "existing", "for",
        "from", "into", "null", "para", "prevent", "should", "still", "the",
        "this", "throw", "throws", "when", "with"
    };

    public ContextPackage Select(
        CodebaseIndex index,
        string taskId,
        string objective,
        IEnumerable<string> allowedScope) =>
        Select(index, taskId, objective, [], allowedScope, []);

    public ContextPackage Select(
        CodebaseIndex index,
        string taskId,
        string objective,
        IEnumerable<string> acceptanceCriteria,
        IEnumerable<string> allowedScope,
        IEnumerable<string> forbiddenScope)
    {
        var allowed = allowedScope.ToList();
        var forbidden = forbiddenScope.ToList();
        var taskText = string.Join(' ', new[] { objective }.Concat(acceptanceCriteria));
        var taskTerms = Tokenize(taskText);
        var normalizedTask = NormalizeSearchText(taskText);

        var allFiles = index.SourceFiles
            .Concat(index.TestFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => allowed.Count > 0 && PathScopeMatcher.MatchesAny(path, allowed))
            .Where(path => forbidden.Count == 0 || !PathScopeMatcher.MatchesAny(path, forbidden))
            .ToList();

        var rankedFiles = allFiles
            .Select(path => new
            {
                Path = path,
                Score = Score(path, index.Symbols, taskTerms, normalizedTask, taskText, allowed)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .ToList();

        // A narrow scope can describe a new feature without sharing words with an
        // existing filename. Preserve a deterministic, scope-safe fallback.
        if (rankedFiles.Count == 0)
        {
            rankedFiles = allFiles
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var symbolsByFile = index.Symbols
            .Where(symbol => rankedFiles.Contains(symbol.FilePath, StringComparer.OrdinalIgnoreCase))
            .GroupBy(symbol => symbol.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(FormatSymbol)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(symbol => symbol, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        var selectedSymbols = rankedFiles
            .Where(symbolsByFile.ContainsKey)
            .SelectMany(path => symbolsByFile[path])
            .ToList();
        var relevantTests = rankedFiles
            .Where(IsTestFile)
            .ToList();

        long totalCharacters = rankedFiles
            .Select(path => Path.Combine(index.RootPath, path))
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);
        var estimatedTokens = (int)Math.Min(int.MaxValue, (totalCharacters + 3) / 4);

        var contextIdentity = string.Join('\n', new[] { taskId, objective }.Concat(rankedFiles));
        var contextHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(contextIdentity)))
            .ToLowerInvariant();

        return new ContextPackage
        {
            Id = $"CTX-{contextHash[..12]}",
            TaskId = taskId,
            SelectedFiles = rankedFiles,
            SelectedSymbols = selectedSymbols,
            RelevantTests = relevantTests,
            SymbolsByFile = symbolsByFile,
            EstimatedTokens = estimatedTokens,
            EligibleFileCount = allFiles.Count,
            Strategy = "scope+task-relevance+tests"
        };
    }

    private static int Score(
        string path,
        IReadOnlyCollection<CodeSymbol> symbols,
        IReadOnlySet<string> taskTerms,
        string normalizedTask,
        string taskText,
        IReadOnlyCollection<string> allowedPatterns)
    {
        var score = 0;
        var normalizedPath = NormalizeSearchText(path);
        var fileName = NormalizeSearchText(Path.GetFileNameWithoutExtension(path));
        var fileSymbols = symbols
            .Where(symbol => string.Equals(symbol.FilePath, path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fileName.Length > 2 && normalizedTask.Contains(fileName, StringComparison.Ordinal))
            score += 120;

        foreach (var term in taskTerms)
        {
            if (normalizedPath.Contains(term, StringComparison.Ordinal))
                score += 12;
            if (fileSymbols.Any(symbol =>
                    NormalizeSearchText(symbol.Name).Contains(term, StringComparison.Ordinal)))
            {
                score += 18;
            }
        }

        foreach (var role in new[] { "handler", "command", "model", "validator", "test" })
        {
            if (taskText.Contains(role, StringComparison.OrdinalIgnoreCase) &&
                path.Contains(role, StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }
        }

        if (IsTestFile(path) && taskText.Contains("test", StringComparison.OrdinalIgnoreCase))
            score += 45;
        if (allowedPatterns.Any(pattern =>
                !pattern.Contains('*') && PathScopeMatcher.Matches(path, pattern)))
        {
            score += 25;
        }

        return score;
    }

    private static IReadOnlySet<string> Tokenize(string value)
    {
        var expanded = Regex.Replace(value, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return Regex.Matches(expanded.ToLowerInvariant(), "[a-z0-9]+")
            .Select(match => match.Value)
            .Where(term => term.Length >= 3 && !StopWords.Contains(term))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeSearchText(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string FormatSymbol(CodeSymbol symbol)
    {
        var qualifiedName = string.IsNullOrWhiteSpace(symbol.Namespace)
            ? symbol.Name
            : $"{symbol.Namespace}.{symbol.Name}";
        var methods = symbol.Methods.Count == 0
            ? string.Empty
            : $" methods=[{string.Join(", ", symbol.Methods)}]";
        return $"{symbol.Kind} {qualifiedName}{methods}";
    }

    private static bool IsTestFile(string path) =>
        path.Replace('\\', '/').Split('/').Any(segment =>
            segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Tests", StringComparison.OrdinalIgnoreCase));
}
