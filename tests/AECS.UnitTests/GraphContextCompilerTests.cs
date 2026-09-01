using AECS.Application.ContextCompiler;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class GraphContextCompilerTests
{
    [Fact]
    public void Compile_RanksDependenciesAcrossProjectsAndStopsCyclesAtConfiguredDepth()
    {
        using var repository = new ContextRepository();
        repository.Write("src/App/OrderHandler.cs", "namespace App; public class OrderHandler { }");
        repository.Write("src/Data/Store.cs", "namespace Data; public class SqlStore { }");
        repository.Write("tests/App/OrderHandlerTests.cs", "namespace App.Tests; public class OrderHandlerTests { }");
        repository.Write("src/Other/Independent.cs", "namespace Other; public class Independent { }");
        var graph = Graph(
            Nodes(
                ("handler", "OrderHandler", "src/App/OrderHandler.cs", "src/App/App.csproj"),
                ("store", "SqlStore", "src/Data/Store.cs", "src/Data/Data.csproj"),
                ("tests", "OrderHandlerTests", "tests/App/OrderHandlerTests.cs", "tests/App/App.Tests.csproj"),
                ("other", "Independent", "src/Other/Independent.cs", "src/Other/Other.csproj")),
            [
                Edge("handler-store", "references", "handler", "store"),
                Edge("store-handler", "references", "store", "handler"),
                Edge("tests-handler", "references", "tests", "handler")
            ]);

        var result = new RepositoryContextCompiler().Compile(
            repository.Path,
            Contract("Change OrderHandler behavior and keep related tests", ["src/**", "tests/**"]),
            "baseline-graph",
            new ContextCompilationOptions { DependencyDepth = 1 },
            graph);

        var store = result.Manifest.Selections.Single(item => item.Path == "src/Data/Store.cs");
        var unrelated = result.Manifest.Selections.Single(item => item.Path == "src/Other/Independent.cs");
        store.Depth.Should().Be(1);
        store.Relation.Should().Be("references");
        store.Rank.Should().BeLessThan(unrelated.Rank);
        unrelated.Depth.Should().Be(-1);
        result.Manifest.Files.Single(file => file.Path == "src/Data/Store.cs")
            .Symbols.Should().Contain(symbol => symbol.Contains("src/Data/Data.csproj"));
        result.Manifest.Selections.Should().Contain(item =>
            item.Path == "tests/App/OrderHandlerTests.cs" && item.Decision == "included");
        result.Manifest.DependencyDepth.Should().Be(1);
        result.Manifest.SymbolGraphHash.Should().Be(graph.GraphHash);
        result.Manifest.RepositorySnapshotHash.Should().Be(graph.RepositorySnapshotHash);
    }

    [Fact]
    public void Compile_UsesExactModelTokenizerAndNeverExceedsEffectiveBudget()
    {
        using var repository = new ContextRepository();
        repository.Write(
            "src/Large/LargeHandler.cs",
            "namespace Demo; public class LargeHandler { /*" + new string('á', 2_000) + "*/ }");
        var counter = new CharacterTokenCounter();
        var compiler = new RepositoryContextCompiler(
            new ModelTokenCounterResolver([counter]));
        var profile = new AgentContextProfile
        {
            Adapter = "exact-adapter",
            Model = "exact-model",
            TokenizerId = counter.Id,
            ContextWindowTokens = 1_500,
            ReservedOutputTokens = 200,
            PromptOverheadTokens = 100
        };

        var result = compiler.Compile(
            repository.Path,
            Contract("Change LargeHandler", ["src/**"]),
            "baseline-exact",
            new ContextCompilationOptions
            {
                MaxTokens = 1_000,
                MaxCharacters = 2_000,
                MaxFileCharacters = 1_000,
                MaxFileTokens = 400
            },
            agentProfile: profile);

        result.Manifest.Tokenizer.Should().Be(counter.Id);
        result.Manifest.ExactTokenCount.Should().BeTrue();
        result.Manifest.Adapter.Should().Be(profile.Adapter);
        result.Manifest.Model.Should().Be(profile.Model);
        result.Manifest.MaxTokens.Should().Be(1_000);
        result.Manifest.EstimatedTokens.Should().Be(counter.CountTokens(result.Prompt));
        result.Manifest.EstimatedTokens.Should().BeLessThanOrEqualTo(result.Manifest.MaxTokens);
        result.Manifest.Files.Should().ContainSingle(file =>
            file.Truncated && file.IncludedTokens <= result.Manifest.MaxFileTokens);
        result.Manifest.ManifestHash.Should().Be(ContextManifestFingerprint.Create(result.Manifest));
    }

    [Fact]
    public void Compile_MinimumBudgetProducesAuditableEmptyPackageInsteadOfOverflow()
    {
        using var repository = new ContextRepository();
        repository.Write("src/Small.cs", "namespace Demo; public class Small { }");
        var profile = new AgentContextProfile
        {
            Adapter = "tiny",
            Model = "tiny-model",
            ContextWindowTokens = 10,
            ReservedOutputTokens = 5,
            PromptOverheadTokens = 5
        };

        var result = new RepositoryContextCompiler().Compile(
            repository.Path,
            Contract("Change Small", ["src/**"]),
            "baseline-tiny",
            new ContextCompilationOptions { MaxTokens = 100 },
            agentProfile: profile);

        result.Prompt.Should().BeEmpty();
        result.CodeContext.Should().BeEmpty();
        result.Manifest.MaxTokens.Should().Be(0);
        result.Manifest.EstimatedTokens.Should().Be(0);
        result.Manifest.Selections.Should().ContainSingle(selection =>
            selection.Decision == "omitted" &&
            selection.Reason == "minimum-budget-header-omitted" &&
            selection.OriginalSha256.StartsWith("sha256:"));
        result.Manifest.OmittedFileCount.Should().Be(1);
        result.Manifest.ManifestHash.Should().Be(ContextManifestFingerprint.Create(result.Manifest));
    }

    [Fact]
    public void Compile_UnknownTokenizerUsesConservativeUtf8FallbackDeterministically()
    {
        using var repository = new ContextRepository();
        repository.Write("src/Unicode.cs", "namespace Demo; // ação\npublic class Unicode { }");
        var profile = new AgentContextProfile
        {
            Adapter = "custom",
            Model = "unknown-model",
            TokenizerId = "missing-tokenizer",
            ContextWindowTokens = 2_000,
            ReservedOutputTokens = 200,
            PromptOverheadTokens = 100
        };
        var compiler = new RepositoryContextCompiler();

        var first = compiler.Compile(
            repository.Path,
            Contract("Change Unicode", ["src/**"]),
            "baseline-fallback",
            agentProfile: profile);
        var second = compiler.Compile(
            repository.Path,
            Contract("Change Unicode", ["src/**"]),
            "baseline-fallback",
            agentProfile: profile);

        first.Manifest.Tokenizer.Should().Be(ConservativeTokenCounter.Id);
        first.Manifest.ExactTokenCount.Should().BeFalse();
        first.Manifest.EstimatedTokens.Should().Be(ConservativeTokenCounter.Count(first.Prompt));
        first.Manifest.ManifestHash.Should().Be(second.Manifest.ManifestHash);
        first.Prompt.Should().Be(second.Prompt);
    }

    private static TaskContract Contract(string objective, List<string> allowed) => new()
    {
        Id = "CTX-GRAPH",
        Objective = objective,
        AcceptanceCriteria = ["Related tests pass"],
        Scope = new ScopeDefinition { Allowed = allowed },
        Budget = ExecutionBudget.Default
    };

    private static List<CSharpSymbolGraphNode> Nodes(
        params (string Id, string Name, string Path, string Project)[] definitions) =>
        definitions.Select(item => new CSharpSymbolGraphNode
        {
            Id = item.Id,
            Hash = "sha256:" + new string('a', 64),
            Kind = "type",
            Name = item.Name,
            DisplayName = $"Demo.{item.Name}",
            TypeKind = "Class",
            ProjectPath = item.Project,
            FilePaths = [item.Path]
        }).ToList();

    private static CSharpSymbolGraphEdge Edge(
        string id,
        string kind,
        string from,
        string to) => new()
        {
            Id = id,
            Hash = "sha256:" + new string('b', 64),
            Kind = kind,
            FromNodeId = from,
            ToNodeId = to
        };

    private static CSharpSymbolGraph Graph(
        List<CSharpSymbolGraphNode> nodes,
        List<CSharpSymbolGraphEdge> edges)
    {
        var draft = new CSharpSymbolGraph
        {
            RepositorySnapshotHash = "sha256:" + new string('c', 64),
            BaselineCommit = "baseline-graph",
            LoadSucceeded = true,
            Nodes = nodes,
            Edges = edges
        };
        return new CSharpSymbolGraph
        {
            GraphHash = CSharpSymbolGraphFingerprint.Create(draft),
            RepositorySnapshotHash = draft.RepositorySnapshotHash,
            BaselineCommit = draft.BaselineCommit,
            LoadSucceeded = draft.LoadSucceeded,
            Nodes = draft.Nodes,
            Edges = draft.Edges
        };
    }

    private sealed class CharacterTokenCounter : ITokenCounter
    {
        public string Id => "character-test-tokenizer";
        public string Version => "1";
        public bool IsExact => true;
        public bool Supports(string adapter, string model) =>
            adapter == "exact-adapter" && model == "exact-model";
        public int CountTokens(string value) => value.Length;
    }

    private sealed class ContextRepository : IDisposable
    {
        public ContextRepository()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aecs-graph-context-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
