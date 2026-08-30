namespace AECS.Application.ContextCompiler;

public class ContextPackage
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public List<string> SelectedFiles { get; init; } = [];
    public List<string> SelectedSymbols { get; init; } = [];
    public List<string> RelevantTests { get; init; } = [];
    public int EstimatedTokens { get; init; }
    public string Strategy { get; init; } = "default";
}

public class ContextSelector
{
    private static readonly HashSet<string> HighValueKeywords =
    [
        "customer", "order", "billing", "payment", "auth", "user",
        "service", "repository", "controller", "mapper", "validator"
    ];

    public ContextPackage Select(CodebaseIndex index, string taskId, string objective, IEnumerable<string> allowedScope)
    {
        var selectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedSymbols = new List<string>();
        var relevantTests = new List<string>();

        var objectiveLower = objective.ToLowerInvariant();

        // 1. Files matching allowed scope
        foreach (var pattern in allowedScope)
        {
            var matchingFiles = index.SourceFiles
                .Concat(index.TestFiles)
                .Where(f => MatchesScope(f, pattern));
            foreach (var f in matchingFiles)
                selectedFiles.Add(f);
        }

        // 2. Files mentioned in objective (keyword matching)
        foreach (var file in index.SourceFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (objectiveLower.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                selectedFiles.Add(file);
        }

        // 3. Symbols in selected files
        foreach (var symbol in index.Symbols)
        {
            if (selectedFiles.Contains(symbol.FilePath))
            {
                selectedSymbols.Add($"{symbol.Kind} {symbol.Namespace}.{symbol.Name}");
            }
        }

        // 4. Tests associated with selected source files
        foreach (var testFile in index.TestFiles)
        {
            var testFileName = Path.GetFileNameWithoutExtension(testFile);
            foreach (var sourceFile in selectedFiles.Where(f => !IsTestFile(f)))
            {
                var sourceName = Path.GetFileNameWithoutExtension(sourceFile);
                if (testFileName.Contains(sourceName, StringComparison.OrdinalIgnoreCase))
                {
                    relevantTests.Add(testFile);
                    selectedFiles.Add(testFile);
                    break;
                }
            }
        }

        // 5. Estimate tokens (rough: ~4 chars per token)
        var totalChars = selectedFiles
            .Where(f => File.Exists(Path.Combine(index.RootPath, f)))
            .Sum(f => new FileInfo(Path.Combine(index.RootPath, f)).Length);
        var estimatedTokens = (int)(totalChars / 4);

        var id = $"CTX-{Guid.NewGuid():N}"[..15];

        return new ContextPackage
        {
            Id = id,
            TaskId = taskId,
            SelectedFiles = selectedFiles.Order().ToList(),
            SelectedSymbols = selectedSymbols,
            RelevantTests = relevantTests,
            EstimatedTokens = estimatedTokens,
            Strategy = "scope+keyword+tests"
        };
    }

    private static bool MatchesScope(string filePath, string pattern)
    {
        filePath = filePath.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');

        if (pattern.EndsWith("/**"))
        {
            var prefix = pattern[..^3];
            return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains('*'))
        {
            var regexPattern = "^" + pattern
                .Replace(".", "\\.")
                .Replace("**", ".*")
                .Replace("*", "[^/]*")
                + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(filePath, regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return string.Equals(filePath, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestFile(string path)
    {
        return path.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }
}
