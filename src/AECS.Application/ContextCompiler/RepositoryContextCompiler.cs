using System.Security.Cryptography;
using System.Text;
using AECS.Domain.Models;

namespace AECS.Application.ContextCompiler;

public sealed class ContextCompilationOptions
{
    public const int DefaultMaxTokens = 12_000;
    public const int DefaultMaxCharacters = 48_000;
    public const int DefaultMaxFileCharacters = 16_000;
    public const int DefaultMaxFileTokens = 4_000;
    public const int DefaultDependencyDepth = 2;

    public int MaxTokens { get; init; } = DefaultMaxTokens;
    public int MaxCharacters { get; init; } = DefaultMaxCharacters;
    public int MaxFileCharacters { get; init; } = DefaultMaxFileCharacters;
    public int MaxFileTokens { get; init; } = DefaultMaxFileTokens;
    public int DependencyDepth { get; init; } = DefaultDependencyDepth;

    public static ContextCompilationOptions FromBudget(ExecutionBudget budget)
    {
        var maxTokens = budget.MaxTokens > 0
            ? Math.Min(DefaultMaxTokens, budget.MaxTokens)
            : DefaultMaxTokens;
        return new ContextCompilationOptions
        {
            MaxTokens = maxTokens,
            MaxCharacters = DefaultMaxCharacters,
            MaxFileCharacters = DefaultMaxFileCharacters,
            MaxFileTokens = Math.Min(DefaultMaxFileTokens, maxTokens)
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
    private const string TruncationMarker = "\n// [AECS CONTEXT TRUNCATED]\n";
    private readonly CodebaseIndexer _indexer;
    private readonly ContextSelector _selector;
    private readonly ITokenCounterResolver _tokenCounterResolver;
    private readonly ContextCompilationOptions? _defaultOptions;
    private readonly ContextSelectionMode _selectionMode;

    public RepositoryContextCompiler(
        ITokenCounterResolver? tokenCounterResolver = null,
        CodebaseIndexer? indexer = null,
        ContextSelector? selector = null,
        ContextCompilationOptions? defaultOptions = null,
        string selectionStrategy = ContextStrategyIds.GraphRanked)
    {
        _tokenCounterResolver = tokenCounterResolver ?? new ModelTokenCounterResolver();
        _indexer = indexer ?? new CodebaseIndexer();
        _selector = selector ?? new ContextSelector();
        _defaultOptions = defaultOptions;
        _selectionMode = ContextStrategyIds.Parse(selectionStrategy);
    }

    public CompiledRepositoryContext Compile(
        string workspacePath,
        TaskContract contract,
        string baselineCommit,
        ContextCompilationOptions? options = null,
        CSharpSymbolGraph? symbolGraph = null,
        AgentContextProfile? agentProfile = null,
        IReadOnlyList<ConstraintRecord>? constraints = null)
    {
        var resolvedRoot = Path.GetFullPath(workspacePath);
        options ??= _defaultOptions ?? ContextCompilationOptions.FromBudget(contract.Budget);
        ValidateOptions(options);
        if (symbolGraph is not null && !string.Equals(
                symbolGraph.BaselineCommit,
                baselineCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "C# symbol graph baseline does not match the context baseline.");
        }

        agentProfile ??= new AgentContextProfile
        {
            Adapter = "standalone",
            Model = "unspecified",
            ContextWindowTokens = options.MaxTokens
        };
        var tokenCounter = _tokenCounterResolver.Resolve(agentProfile);
        var effectiveTokenLimit = Math.Min(
            options.MaxTokens,
            agentProfile.EffectiveContextTokens(contract.Budget));
        var characterLimit = options.MaxCharacters;
        var perFileTokenLimit = Math.Min(options.MaxFileTokens, effectiveTokenLimit);

        var index = _indexer.Index(
            resolvedRoot,
            _selectionMode == ContextSelectionMode.GraphRanked ? symbolGraph : null);
        var package = _selector.Select(
            index,
            contract.Id,
            contract.Objective,
            contract.AcceptanceCriteria,
            contract.Scope.Allowed,
            contract.Scope.Forbidden,
            new ContextSelectionOptions
            {
                DependencyDepth = options.DependencyDepth,
                Mode = _selectionMode
            });
        var strategy = $"{package.Strategy}+{index.Source}";
        var header = BuildHeader(
            contract.Id,
            baselineCommit,
            strategy,
            agentProfile,
            tokenCounter,
            effectiveTokenLimit,
            options.DependencyDepth);
        var headerFits = effectiveTokenLimit > 0 &&
            header.Length <= characterLimit &&
            tokenCounter.CountTokens(header) <= effectiveTokenLimit;
        var constraintSection = BuildConstraintSection(constraints);
        var prompt = new StringBuilder(constraintSection);
        if (headerFits)
        {
            prompt.Append(header);
        }

        var codeContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileManifests = new List<ContextFileManifest>();
        var selections = new List<ContextSelectionManifest>();

        foreach (var candidate in package.RankedFiles)
        {
            var relativePath = candidate.Path.Replace('\\', '/');
            var symbols = package.SymbolsByFile.TryGetValue(relativePath, out var selectedSymbols)
                ? selectedSymbols
                : [];
            if (IsSensitive(relativePath))
            {
                selections.Add(Omitted(candidate, "sensitive-path-filter"));
                continue;
            }

            var fullPath = ResolveFileInsideRoot(resolvedRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                selections.Add(Omitted(candidate, "file-not-found"));
                continue;
            }

            var originalContent = File.ReadAllText(fullPath);
            var originalHash = Hash(originalContent);
            var originalTokens = tokenCounter.CountTokens(originalContent);
            if (!headerFits)
            {
                selections.Add(Omitted(
                    candidate,
                    "minimum-budget-header-omitted",
                    originalHash,
                    originalTokens));
                continue;
            }

            var prefix = BuildFilePrefix(candidate, originalHash, symbols);
            const string suffix = "\n```\n\n";
            var fullBlock = prefix + originalContent + suffix;
            var fullFits = originalContent.Length <= options.MaxFileCharacters &&
                originalTokens <= perFileTokenLimit &&
                Fits(prompt, fullBlock, characterLimit, effectiveTokenLimit, tokenCounter);

            string? includedContent;
            var truncated = false;
            var reason = "ranked-within-effective-budget";
            if (fullFits)
            {
                includedContent = originalContent;
            }
            else
            {
                includedContent = FindLargestTruncatedContent(
                    originalContent,
                    prefix,
                    suffix,
                    prompt,
                    options.MaxFileCharacters,
                    perFileTokenLimit,
                    characterLimit,
                    effectiveTokenLimit,
                    tokenCounter);
                truncated = includedContent is not null;
                reason = includedContent is null
                    ? "effective-token-budget-exhausted"
                    : TruncationReason(
                        originalContent,
                        originalTokens,
                        options,
                        perFileTokenLimit);
            }

            if (includedContent is null)
            {
                selections.Add(Omitted(
                    candidate,
                    reason,
                    originalHash,
                    originalTokens));
                continue;
            }

            var includedHash = Hash(includedContent);
            var includedTokens = tokenCounter.CountTokens(includedContent);
            prompt.Append(prefix).Append(includedContent).Append(suffix);
            codeContext[relativePath] = includedContent;
            fileManifests.Add(new ContextFileManifest
            {
                Path = relativePath,
                Sha256 = originalHash,
                IncludedSha256 = includedHash,
                OriginalCharacters = originalContent.Length,
                IncludedCharacters = includedContent.Length,
                OriginalTokens = originalTokens,
                IncludedTokens = includedTokens,
                Truncated = truncated,
                Rank = candidate.Rank,
                Score = candidate.Score,
                Depth = candidate.Depth,
                Relation = candidate.Relation,
                Reasons = candidate.Reasons.ToList(),
                Symbols = symbols.ToList()
            });
            selections.Add(new ContextSelectionManifest
            {
                Path = relativePath,
                Decision = truncated ? "truncated" : "included",
                Reason = reason,
                Rank = candidate.Rank,
                Score = candidate.Score,
                Depth = candidate.Depth,
                Relation = candidate.Relation,
                RankingReasons = candidate.Reasons.ToList(),
                OriginalSha256 = originalHash,
                IncludedSha256 = includedHash,
                OriginalTokens = originalTokens,
                IncludedTokens = includedTokens
            });
        }

        var promptText = prompt.ToString();
        var estimatedTokens = tokenCounter.CountTokens(promptText);
        var obligationTokens = constraintSection.Length == 0
            ? 0
            : tokenCounter.CountTokens(constraintSection);
        if (estimatedTokens - obligationTokens > effectiveTokenLimit ||
            promptText.Length - constraintSection.Length > characterLimit)
        {
            throw new InvalidOperationException("Compiled context exceeded its effective adapter budget.");
        }

        // Manifest metrics describe the selected repository context. The constraint
        // obligation section is authoritative regardless of budget and is tracked
        // separately via the evidence ConstraintSet.
        var selectedTextLength = promptText.Length - constraintSection.Length;
        var selectedTokenCount = estimatedTokens - obligationTokens;

        var omittedFileCount = selections.Count(selection => selection.Decision == "omitted");
        var draft = new ContextManifest
        {
            SchemaVersion = ContextManifestSchema.CurrentVersion,
            StrategyVersion = _selectionMode == ContextSelectionMode.GraphRanked
                ? ContextManifestSchema.StrategyVersion
                : ContextManifestSchema.NaiveStrategyVersion,
            TaskId = contract.Id,
            BaselineCommit = baselineCommit,
            Source = SourceName,
            Strategy = strategy,
            SemanticIndex = index.Source,
            SymbolGraphHash = index.SemanticAuthority ? index.SymbolGraphHash : null,
            RepositorySnapshotHash = index.SemanticAuthority
                ? symbolGraph?.RepositorySnapshotHash
                : null,
            Adapter = agentProfile.Adapter,
            Model = agentProfile.Model,
            Tokenizer = tokenCounter.Id,
            TokenizerVersion = tokenCounter.Version,
            ExactTokenCount = tokenCounter.IsExact,
            ModelContextWindowTokens = agentProfile.ContextWindowTokens,
            ReservedOutputTokens = agentProfile.ReservedOutputTokens,
            PromptOverheadTokens = agentProfile.PromptOverheadTokens,
            DependencyDepth = options.DependencyDepth,
            MaxFileTokens = perFileTokenLimit,
            MaxTokens = effectiveTokenLimit,
            MaxCharacters = characterLimit,
            EstimatedTokens = selectedTokenCount,
            TotalCharacters = selectedTextLength,
            EligibleFileCount = package.EligibleFileCount,
            OmittedFileCount = omittedFileCount,
            Truncated = selections.Any(selection => selection.Decision != "included"),
            Files = fileManifests,
            Selections = selections
        };
        var manifest = WithIdentity(draft);

        return new CompiledRepositoryContext
        {
            CodeContext = codeContext,
            Prompt = promptText,
            Manifest = manifest
        };
    }

    public static ContextManifest EmptyManifest(string taskId, string baselineCommit)
    {
        var draft = new ContextManifest
        {
            SchemaVersion = ContextManifestSchema.CurrentVersion,
            StrategyVersion = ContextManifestSchema.StrategyVersion,
            TaskId = taskId,
            BaselineCommit = baselineCommit,
            Source = SourceName,
            Strategy = "not-compiled",
            SemanticIndex = "not-compiled",
            Tokenizer = ConservativeTokenCounter.Id,
            TokenizerVersion = ConservativeTokenCounter.Version
        };
        return WithIdentity(draft);
    }

    private static string BuildHeader(
        string taskId,
        string baselineCommit,
        string strategy,
        AgentContextProfile profile,
        ITokenCounter counter,
        int effectiveTokenLimit,
        int dependencyDepth)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("## REPOSITORY CONTEXT");
        prompt.AppendLine($"Source: {SourceName}");
        prompt.AppendLine($"Task: {taskId}");
        prompt.AppendLine($"Baseline: {baselineCommit}");
        prompt.AppendLine($"Strategy: {strategy}");
        prompt.AppendLine($"Adapter: {profile.Adapter}");
        prompt.AppendLine($"Model: {profile.Model}");
        prompt.AppendLine($"Tokenizer: {counter.Id}@{counter.Version}");
        prompt.AppendLine($"Effective context limit: {effectiveTokenLimit} tokens");
        prompt.AppendLine($"Dependency depth: {dependencyDepth}");
        prompt.AppendLine();
        return prompt.ToString();
    }

    private static string BuildConstraintSection(
        IReadOnlyList<ConstraintRecord>? constraints)
    {
        if (constraints is null || constraints.Count == 0)
        {
            return string.Empty;
        }

        const int maxLines = 40;
        const int maxDescriptionLength = 160;
        var builder = new StringBuilder();
        builder.AppendLine("## ACTIVE CONSTRAINTS (authoritative — must be honored)");
        foreach (var constraint in constraints
                     .OrderBy(constraint => constraint.RequirementKey, StringComparer.Ordinal)
                     .Take(maxLines))
        {
            var description = constraint.Description.Length > maxDescriptionLength
                ? constraint.Description[..maxDescriptionLength] + "..."
                : constraint.Description;
            builder.AppendLine(
                $"- [{constraint.Kind}/{constraint.Verifiability}] " +
                $"{constraint.RequirementKey} (rev {constraint.Revision}): {description}");
        }

        if (constraints.Count > maxLines)
        {
            builder.AppendLine(
                $"- ... and {constraints.Count - maxLines} more active constraint(s)");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string BuildFilePrefix(
        ContextCandidate candidate,
        string hash,
        IReadOnlyCollection<string> symbols)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"### {candidate.Path}");
        builder.AppendLine($"Rank: {candidate.Rank}; Score: {candidate.Score}; " +
                           $"Depth: {candidate.Depth}; Relation: {candidate.Relation}");
        builder.AppendLine($"SHA256: {hash}");
        if (candidate.Reasons.Count > 0)
            builder.AppendLine($"Reasons: {string.Join(", ", candidate.Reasons)}");
        if (symbols.Count > 0)
        {
            builder.AppendLine("Symbols:");
            foreach (var symbol in symbols)
                builder.AppendLine($"- {symbol}");
        }
        builder.AppendLine("```csharp");
        return builder.ToString();
    }

    private static string? FindLargestTruncatedContent(
        string content,
        string prefix,
        string suffix,
        StringBuilder prompt,
        int maxFileCharacters,
        int maxFileTokens,
        int maxCharacters,
        int maxTokens,
        ITokenCounter tokenCounter)
    {
        if (content.Length == 0 || maxFileCharacters < TruncationMarker.Length ||
            maxFileTokens < tokenCounter.CountTokens(TruncationMarker))
        {
            return null;
        }

        var low = 0;
        var high = Math.Min(content.Length - 1, maxFileCharacters - TruncationMarker.Length);
        string? best = null;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var safeLength = SafePrefixLength(content, middle);
            var candidate = content[..safeLength] + TruncationMarker;
            var fits = tokenCounter.CountTokens(candidate) <= maxFileTokens &&
                Fits(prompt, prefix + candidate + suffix, maxCharacters, maxTokens, tokenCounter);
            if (fits)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private static int SafePrefixLength(string content, int length)
    {
        if (length > 0 && length < content.Length && char.IsHighSurrogate(content[length - 1]))
            return length - 1;
        return length;
    }

    private static bool Fits(
        StringBuilder current,
        string addition,
        int maxCharacters,
        int maxTokens,
        ITokenCounter tokenCounter)
    {
        if (current.Length + addition.Length > maxCharacters)
            return false;
        return tokenCounter.CountTokens(current.ToString() + addition) <= maxTokens;
    }

    private static string TruncationReason(
        string originalContent,
        int originalTokens,
        ContextCompilationOptions options,
        int perFileTokenLimit)
    {
        if (originalContent.Length > options.MaxFileCharacters &&
            originalTokens > perFileTokenLimit)
        {
            return "per-file-character-and-token-limits";
        }
        if (originalContent.Length > options.MaxFileCharacters)
            return "per-file-character-limit";
        if (originalTokens > perFileTokenLimit)
            return "per-file-token-limit";
        return "effective-token-budget-truncation";
    }

    private static ContextSelectionManifest Omitted(
        ContextCandidate candidate,
        string reason,
        string originalHash = "",
        int originalTokens = 0) => new()
        {
            Path = candidate.Path,
            Decision = "omitted",
            Reason = reason,
            Rank = candidate.Rank,
            Score = candidate.Score,
            Depth = candidate.Depth,
            Relation = candidate.Relation,
            RankingReasons = candidate.Reasons.ToList(),
            OriginalSha256 = originalHash,
            OriginalTokens = originalTokens
        };

    private static ContextManifest WithIdentity(ContextManifest draft)
    {
        var hash = ContextManifestFingerprint.Create(draft);
        return new ContextManifest
        {
            SchemaVersion = draft.SchemaVersion,
            StrategyVersion = draft.StrategyVersion,
            Id = $"CTX-{hash[7..19]}",
            TaskId = draft.TaskId,
            BaselineCommit = draft.BaselineCommit,
            Source = draft.Source,
            Strategy = draft.Strategy,
            SemanticIndex = draft.SemanticIndex,
            SymbolGraphHash = draft.SymbolGraphHash,
            RepositorySnapshotHash = draft.RepositorySnapshotHash,
            Adapter = draft.Adapter,
            Model = draft.Model,
            Tokenizer = draft.Tokenizer,
            TokenizerVersion = draft.TokenizerVersion,
            ExactTokenCount = draft.ExactTokenCount,
            ModelContextWindowTokens = draft.ModelContextWindowTokens,
            ReservedOutputTokens = draft.ReservedOutputTokens,
            PromptOverheadTokens = draft.PromptOverheadTokens,
            DependencyDepth = draft.DependencyDepth,
            MaxFileTokens = draft.MaxFileTokens,
            MaxTokens = draft.MaxTokens,
            MaxCharacters = draft.MaxCharacters,
            EstimatedTokens = draft.EstimatedTokens,
            TotalCharacters = draft.TotalCharacters,
            EligibleFileCount = draft.EligibleFileCount,
            OmittedFileCount = draft.OmittedFileCount,
            Truncated = draft.Truncated,
            ManifestHash = hash,
            Files = draft.Files,
            Selections = draft.Selections
        };
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
            options.MaxFileCharacters <= 0 || options.MaxFileTokens <= 0 ||
            options.DependencyDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Context limits must be positive and dependency depth cannot be negative.");
        }
    }

    private static string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
