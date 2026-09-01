using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AECS.Application.ContextCompiler;

public sealed class ContextSelectionOptions
{
    public int DependencyDepth { get; init; } = 2;
}

public sealed class ContextCandidate
{
    public string Path { get; init; } = string.Empty;
    public int Rank { get; init; }
    public int Score { get; init; }
    public int Depth { get; init; } = -1;
    public string Relation { get; init; } = string.Empty;
    public List<string> Reasons { get; init; } = [];
}

public class ContextPackage
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public List<string> SelectedFiles { get; init; } = [];
    public List<ContextCandidate> RankedFiles { get; init; } = [];
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
        Select(index, taskId, objective, [], allowedScope, [], null);

    public ContextPackage Select(
        CodebaseIndex index,
        string taskId,
        string objective,
        IEnumerable<string> acceptanceCriteria,
        IEnumerable<string> allowedScope,
        IEnumerable<string> forbiddenScope,
        ContextSelectionOptions? options = null)
    {
        options ??= new ContextSelectionOptions();
        if (options.DependencyDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Dependency depth cannot be negative.");

        var acceptance = acceptanceCriteria.ToList();
        var allowed = allowedScope.ToList();
        var forbidden = forbiddenScope.ToList();
        var taskText = string.Join(' ', new[] { objective }.Concat(acceptance));
        var taskTerms = Tokenize(taskText);
        var normalizedTask = NormalizeSearchText(taskText);

        var allFiles = index.SourceFiles
            .Concat(index.TestFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => allowed.Count > 0 && PathScopeMatcher.MatchesAny(path, allowed))
            .Where(path => forbidden.Count == 0 || !PathScopeMatcher.MatchesAny(path, forbidden))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var eligible = allFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = allFiles.ToDictionary(
            path => path,
            path => DirectScore(path, index.Symbols, taskTerms, normalizedTask, taskText, allowed),
            StringComparer.OrdinalIgnoreCase);

        var seeds = states.Values
            .Where(state => state.Score > 0)
            .OrderByDescending(state => state.Score)
            .ThenBy(state => state.Path, StringComparer.Ordinal)
            .ToList();

        // A narrow scope can describe a new feature without sharing words with an
        // existing filename. Every in-scope file then becomes a deterministic seed.
        if (seeds.Count == 0)
        {
            foreach (var state in states.Values.OrderBy(state => state.Path, StringComparer.Ordinal))
            {
                state.Score = 1;
                state.Depth = 0;
                state.Relation = "scope-fallback";
                state.Reasons.Add("scope-eligible-fallback");
            }
            seeds = states.Values.OrderBy(state => state.Path, StringComparer.Ordinal).ToList();
        }
        else
        {
            foreach (var seed in seeds)
            {
                seed.Depth = 0;
                seed.Relation = "task-match";
            }
        }

        ExpandGraph(index, eligible, states, seeds, options.DependencyDepth);

        foreach (var state in states.Values.Where(state => state.Depth < 0))
            state.Reasons.Add("scope-eligible-no-task-or-graph-match");

        var rankedFiles = states.Values
            .OrderByDescending(state => state.Score)
            .ThenBy(state => state.Depth < 0 ? int.MaxValue : state.Depth)
            .ThenBy(state => state.Path, StringComparer.Ordinal)
            .Select((state, position) => new ContextCandidate
            {
                Path = state.Path,
                Rank = position + 1,
                Score = state.Score,
                Depth = state.Depth,
                Relation = state.Relation,
                Reasons = state.Reasons
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(reason => reason, StringComparer.Ordinal)
                    .ToList()
            })
            .ToList();

        var selectedFiles = rankedFiles.Select(candidate => candidate.Path).ToList();
        var symbolsByFile = index.Symbols
            .Where(symbol => eligible.Contains(symbol.FilePath))
            .GroupBy(symbol => symbol.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(FormatSymbol)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(symbol => symbol, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        var selectedSymbols = selectedFiles
            .Where(symbolsByFile.ContainsKey)
            .SelectMany(path => symbolsByFile[path])
            .ToList();
        var relevantTests = selectedFiles.Where(IsTestFile).ToList();

        long estimatedTokens = selectedFiles
            .Select(path => Path.Combine(index.RootPath, path))
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);

        var strategy = index.SemanticAuthority
            ? $"task+scope+semantic-relations-depth-{options.DependencyDepth}"
            : "task+scope+textual-fallback";
        var contextIdentity = new StringBuilder()
            .AppendLine(taskId)
            .AppendLine(objective)
            .AppendLine(string.Join('\n', acceptance))
            .AppendLine(strategy)
            .AppendLine(index.SymbolGraphHash);
        foreach (var candidate in rankedFiles)
        {
            contextIdentity.AppendLine(
                $"{candidate.Path}|{candidate.Rank}|{candidate.Score}|{candidate.Depth}|" +
                $"{candidate.Relation}|{string.Join(';', candidate.Reasons)}");
        }
        var contextHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(contextIdentity.ToString())))
            .ToLowerInvariant();

        return new ContextPackage
        {
            Id = $"CTX-{contextHash[..12]}",
            TaskId = taskId,
            SelectedFiles = selectedFiles,
            RankedFiles = rankedFiles,
            SelectedSymbols = selectedSymbols,
            RelevantTests = relevantTests,
            SymbolsByFile = symbolsByFile,
            EstimatedTokens = (int)Math.Min(int.MaxValue, estimatedTokens),
            EligibleFileCount = allFiles.Count,
            Strategy = strategy
        };
    }

    private static void ExpandGraph(
        CodebaseIndex index,
        IReadOnlySet<string> eligible,
        IReadOnlyDictionary<string, RankedState> states,
        IReadOnlyCollection<RankedState> seeds,
        int maxDepth)
    {
        if (!index.SemanticAuthority || maxDepth == 0)
            return;

        var queue = new Queue<TraversalState>();
        foreach (var seed in seeds)
            queue.Enqueue(new TraversalState(seed.Path, 0, seed.Score));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth >= maxDepth ||
                !index.FileRelations.TryGetValue(current.Path, out var relations))
            {
                continue;
            }

            foreach (var relation in relations
                         .Where(relation => eligible.Contains(relation.ToPath))
                         .OrderBy(relation => relation.ToPath, StringComparer.Ordinal)
                         .ThenBy(relation => relation.Kind, StringComparer.Ordinal))
            {
                var nextDepth = current.Depth + 1;
                var candidate = states[relation.ToPath];
                var graphScore = current.RootScore + RelationWeight(relation.Kind) - nextDepth * 10;
                if (IsTestFile(candidate.Path))
                    graphScore += 30;

                var improves = candidate.Depth < 0 || nextDepth < candidate.Depth ||
                    nextDepth == candidate.Depth && graphScore > candidate.Score;
                if (!improves)
                    continue;

                candidate.Depth = nextDepth;
                candidate.Score = Math.Max(candidate.Score, graphScore);
                candidate.Relation = relation.Kind;
                candidate.Reasons.Add(
                    $"graph:{relation.Kind}:{relation.Symbol}:from:{current.Path}");
                queue.Enqueue(new TraversalState(candidate.Path, nextDepth, current.RootScore));
            }
        }
    }

    private static int RelationWeight(string kind) => kind switch
    {
        "references" => 70,
        "referenced-by:references" => 60,
        "inherits" or "implements" => 65,
        "referenced-by:inherits" or "referenced-by:implements" => 55,
        "partial" => 75,
        _ when kind.StartsWith("referenced-by:", StringComparison.Ordinal) => 35,
        _ => 45
    };

    private static RankedState DirectScore(
        string path,
        IReadOnlyCollection<CodeSymbol> symbols,
        IReadOnlySet<string> taskTerms,
        string normalizedTask,
        string taskText,
        IReadOnlyCollection<string> allowedPatterns)
    {
        var state = new RankedState(path);
        var normalizedPath = NormalizeSearchText(path);
        var fileName = NormalizeSearchText(Path.GetFileNameWithoutExtension(path));
        var fileSymbols = symbols
            .Where(symbol => string.Equals(symbol.FilePath, path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fileName.Length > 2 && normalizedTask.Contains(fileName, StringComparison.Ordinal))
        {
            state.Score += 120;
            state.Reasons.Add("task-filename-match");
        }

        foreach (var term in taskTerms.OrderBy(term => term, StringComparer.Ordinal))
        {
            if (normalizedPath.Contains(term, StringComparison.Ordinal))
            {
                state.Score += 12;
                state.Reasons.Add($"task-path-term:{term}");
            }
            if (fileSymbols.Any(symbol => SymbolText(symbol).Contains(term, StringComparison.Ordinal)))
            {
                state.Score += 18;
                state.Reasons.Add($"task-symbol-term:{term}");
            }
        }

        foreach (var role in new[] { "handler", "command", "model", "validator", "test" })
        {
            if (taskText.Contains(role, StringComparison.OrdinalIgnoreCase) &&
                path.Contains(role, StringComparison.OrdinalIgnoreCase))
            {
                state.Score += 35;
                state.Reasons.Add($"task-role:{role}");
            }
        }

        if (IsTestFile(path) && taskText.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            state.Score += 45;
            state.Reasons.Add("acceptance-test-match");
        }
        if (allowedPatterns.Any(pattern =>
                !pattern.Contains('*') && PathScopeMatcher.Matches(path, pattern)))
        {
            state.Score += 25;
            state.Reasons.Add("exact-allowed-scope");
        }

        return state;
    }

    private static string SymbolText(CodeSymbol symbol) => NormalizeSearchText(string.Join(
        ' ',
        symbol.Name,
        symbol.DisplayName,
        symbol.Namespace,
        symbol.ProjectPath,
        string.Join(' ', symbol.BaseTypes),
        string.Join(' ', symbol.Methods)));

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
        var qualifiedName = !string.IsNullOrWhiteSpace(symbol.DisplayName)
            ? symbol.DisplayName
            : string.IsNullOrWhiteSpace(symbol.Namespace)
                ? symbol.Name
                : $"{symbol.Namespace}.{symbol.Name}";
        var project = string.IsNullOrWhiteSpace(symbol.ProjectPath)
            ? string.Empty
            : $" project={symbol.ProjectPath}";
        var methods = symbol.Methods.Count == 0
            ? string.Empty
            : $" methods=[{string.Join(", ", symbol.Methods)}]";
        return $"{symbol.Kind} {qualifiedName}{project}{methods}";
    }

    private static bool IsTestFile(string path) =>
        path.Replace('\\', '/').Split('/').Any(segment =>
            segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Test", StringComparison.OrdinalIgnoreCase));

    private sealed class RankedState(string path)
    {
        public string Path { get; } = path;
        public int Score { get; set; }
        public int Depth { get; set; } = -1;
        public string Relation { get; set; } = string.Empty;
        public List<string> Reasons { get; } = [];
    }

    private sealed record TraversalState(string Path, int Depth, int RootScore);
}
