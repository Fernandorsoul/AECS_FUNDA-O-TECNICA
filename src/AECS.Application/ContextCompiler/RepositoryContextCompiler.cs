using System.Security.Cryptography;
using System.Text;
using AECS.Domain.Models;

namespace AECS.Application.ContextCompiler;

public sealed class ContextCompilationOptions
{
    public const int DefaultMaxTokens = 12_000;
    public const int DefaultMaxCharacters = 48_000;
    public const int DefaultMaxFileCharacters = 16_000;

    public int MaxTokens { get; init; } = DefaultMaxTokens;
    public int MaxCharacters { get; init; } = DefaultMaxCharacters;
    public int MaxFileCharacters { get; init; } = DefaultMaxFileCharacters;

    public static ContextCompilationOptions FromBudget(ExecutionBudget budget)
    {
        var maxTokens = budget.MaxTokens > 0
            ? Math.Min(DefaultMaxTokens, budget.MaxTokens)
            : DefaultMaxTokens;
        var maxCharacters = Math.Min(DefaultMaxCharacters, checked(maxTokens * 4));

        return new ContextCompilationOptions
        {
            MaxTokens = maxTokens,
            MaxCharacters = maxCharacters,
            MaxFileCharacters = Math.Min(DefaultMaxFileCharacters, maxCharacters)
        };
    }
}

public sealed class CompiledRepositoryContext
{
    public Dictionary<string, string> CodeContext { get; init; } = new();
    public string Prompt { get; init; } = string.Empty;
    public ContextManifest Manifest { get; init; } = new();
}

public sealed class RepositoryContextCompiler
{
    private const string SourceName = "isolated-git-worktree";
    private readonly CodebaseIndexer _indexer = new();
    private readonly ContextSelector _selector = new();

    public CompiledRepositoryContext Compile(
        string workspacePath,
        TaskContract contract,
        string baselineCommit,
        ContextCompilationOptions? options = null)
    {
        var resolvedRoot = Path.GetFullPath(workspacePath);
        options ??= ContextCompilationOptions.FromBudget(contract.Budget);
        ValidateOptions(options);

        var index = _indexer.Index(resolvedRoot);
        var package = _selector.Select(
            index,
            contract.Id,
            contract.Objective,
            contract.AcceptanceCriteria,
            contract.Scope.Allowed,
            contract.Scope.Forbidden);

        var characterLimit = Math.Min(options.MaxCharacters, checked(options.MaxTokens * 4));
        var prompt = new StringBuilder();
        prompt.AppendLine("## REPOSITORY CONTEXT");
        prompt.AppendLine($"Source: {SourceName}");
        prompt.AppendLine($"Task: {contract.Id}");
        prompt.AppendLine($"Baseline: {baselineCommit}");
        prompt.AppendLine($"Strategy: {package.Strategy}");
        prompt.AppendLine($"Limit: {options.MaxTokens} estimated tokens / {characterLimit} characters");
        prompt.AppendLine();

        if (prompt.Length > characterLimit)
            throw new InvalidOperationException("Context limit is too small for the deterministic header.");

        var codeContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileManifests = new List<ContextFileManifest>();
        var budgetTruncated = false;

        foreach (var relativePath in package.SelectedFiles)
        {
            var fullPath = ResolveFileInsideRoot(resolvedRoot, relativePath);
            if (!File.Exists(fullPath) || IsSensitive(relativePath))
                continue;

            var originalContent = File.ReadAllText(fullPath);
            var symbols = package.SymbolsByFile.TryGetValue(relativePath, out var selectedSymbols)
                ? selectedSymbols
                : [];
            var prefix = BuildFilePrefix(relativePath, Hash(originalContent), symbols);
            const string suffix = "\n```\n\n";
            var remaining = characterLimit - prompt.Length;
            var availableContentCharacters = remaining - prefix.Length - suffix.Length;
            if (availableContentCharacters <= 0)
            {
                budgetTruncated = true;
                break;
            }

            var perFileLimit = Math.Min(options.MaxFileCharacters, availableContentCharacters);
            var includedContent = Truncate(originalContent, perFileLimit, out var fileTruncated);
            if (includedContent.Length == 0 && originalContent.Length > 0)
            {
                budgetTruncated = true;
                break;
            }

            prompt.Append(prefix);
            prompt.Append(includedContent);
            prompt.Append(suffix);
            codeContext[relativePath] = includedContent;
            fileManifests.Add(new ContextFileManifest
            {
                Path = relativePath,
                Sha256 = Hash(originalContent),
                IncludedSha256 = Hash(includedContent),
                OriginalCharacters = originalContent.Length,
                IncludedCharacters = includedContent.Length,
                Truncated = fileTruncated,
                Symbols = symbols.ToList()
            });

            budgetTruncated |= fileTruncated;
        }

        if (fileManifests.Count < package.SelectedFiles.Count)
            budgetTruncated = true;

        var promptText = prompt.ToString();
        var estimatedTokens = (promptText.Length + 3) / 4;
        var omittedFileCount = Math.Max(0, package.EligibleFileCount - fileManifests.Count);
        var manifestHash = HashManifest(
            contract.Id,
            baselineCommit,
            package.Strategy,
            options,
            characterLimit,
            estimatedTokens,
            promptText.Length,
            package.EligibleFileCount,
            omittedFileCount,
            budgetTruncated,
            fileManifests);
        var manifest = new ContextManifest
        {
            Id = $"CTX-{manifestHash[7..19]}",
            TaskId = contract.Id,
            BaselineCommit = baselineCommit,
            Source = SourceName,
            Strategy = package.Strategy,
            MaxTokens = options.MaxTokens,
            MaxCharacters = characterLimit,
            EstimatedTokens = estimatedTokens,
            TotalCharacters = promptText.Length,
            EligibleFileCount = package.EligibleFileCount,
            OmittedFileCount = omittedFileCount,
            Truncated = budgetTruncated,
            ManifestHash = manifestHash,
            Files = fileManifests
        };

        return new CompiledRepositoryContext
        {
            CodeContext = codeContext,
            Prompt = promptText,
            Manifest = manifest
        };
    }

    public static ContextManifest EmptyManifest(string taskId, string baselineCommit)
    {
        var hash = HashManifest(
            taskId,
            baselineCommit,
            "not-compiled",
            new ContextCompilationOptions(),
            0,
            0,
            0,
            0,
            0,
            false,
            []);
        return new ContextManifest
        {
            Id = $"CTX-{hash[7..19]}",
            TaskId = taskId,
            BaselineCommit = baselineCommit,
            Source = SourceName,
            Strategy = "not-compiled",
            ManifestHash = hash
        };
    }

    private static string BuildFilePrefix(
        string path,
        string hash,
        IReadOnlyCollection<string> symbols)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"### {path}");
        builder.AppendLine($"SHA256: {hash}");
        if (symbols.Count > 0)
        {
            builder.AppendLine("Symbols:");
            foreach (var symbol in symbols)
                builder.AppendLine($"- {symbol}");
        }
        builder.AppendLine("```csharp");
        return builder.ToString();
    }

    private static string Truncate(string content, int limit, out bool truncated)
    {
        const string marker = "\n// [AECS CONTEXT TRUNCATED]\n";
        if (content.Length <= limit)
        {
            truncated = false;
            return content;
        }

        truncated = true;
        if (limit <= marker.Length)
            return string.Empty;
        return content[..(limit - marker.Length)] + marker;
    }

    private static string ResolveFileInsideRoot(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"Context path must be repository-relative: {relativePath}");

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var rootPrefix = rootPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
            throw new InvalidOperationException($"Context path escaped the isolated worktree: {relativePath}");

        return fullPath;
    }

    private static bool IsSensitive(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.LastOrDefault() ?? string.Empty;
        return segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) ||
            fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateOptions(ContextCompilationOptions options)
    {
        if (options.MaxTokens <= 0 || options.MaxCharacters <= 0 ||
            options.MaxFileCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Context token, character, and per-file limits must be positive.");
        }
    }

    private static string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static string HashManifest(
        string taskId,
        string baselineCommit,
        string strategy,
        ContextCompilationOptions options,
        int characterLimit,
        int estimatedTokens,
        int totalCharacters,
        int eligibleFileCount,
        int omittedFileCount,
        bool truncated,
        IEnumerable<ContextFileManifest> files)
    {
        var canonical = new StringBuilder();
        canonical.AppendLine(taskId);
        canonical.AppendLine(baselineCommit);
        canonical.AppendLine(strategy);
        canonical.AppendLine($"{options.MaxTokens}|{characterLimit}|{options.MaxFileCharacters}");
        canonical.AppendLine($"{estimatedTokens}|{totalCharacters}|{eligibleFileCount}|{omittedFileCount}|{truncated}");
        foreach (var file in files)
        {
            canonical.AppendLine(
                $"{file.Path}|{file.Sha256}|{file.IncludedSha256}|{file.OriginalCharacters}|" +
                $"{file.IncludedCharacters}|{file.Truncated}|{string.Join(';', file.Symbols)}");
        }

        return Hash(canonical.ToString());
    }
}
