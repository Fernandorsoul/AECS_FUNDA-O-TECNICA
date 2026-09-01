using AECS.Application.ContextCompiler;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class CodebaseIndexerTests
{
    private readonly CodebaseIndexer _indexer = new();

    [Fact]
    public void Index_SampleProject_FindsSourceFiles()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return; // Skip if SampleProject not available

        var index = _indexer.Index(Path.Combine(samplePath, "src", "SampleProject"));

        index.SourceFiles.Should().NotBeEmpty();
        index.SourceFiles.Should().Contain(f => f.Contains("CustomerMapper"));
        index.SourceFiles.Should().Contain(f => f.Contains("CustomerService"));
        index.SemanticAuthority.Should().BeFalse();
        index.Symbols.Should().BeEmpty("textual fallback is only a file inventory");
    }

    [Fact]
    public void Index_RoslynGraph_ProjectsAuthoritativeSymbols()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var index = _indexer.Index(
            Path.Combine(samplePath, "src", "SampleProject"),
            SemanticGraph());

        index.SemanticAuthority.Should().BeTrue();
        index.Symbols.Should().Contain(s => s.Name == "Customer");
        index.Symbols.Single(s => s.Name == "Customer").Methods.Should().Equal("Map");
    }

    [Fact]
    public void Index_SampleProject_IdentifiesTestFiles()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var index = _indexer.Index(Path.Combine(samplePath, "tests", "SampleProject.Tests"));

        index.TestFiles.Should().NotBeEmpty();
        index.TestFiles.Should().Contain(f => f.Contains("CustomerMapperTests"));
    }

    [Fact]
    public void Index_SampleProject_ExtractsNamespaces()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var index = _indexer.Index(
            Path.Combine(samplePath, "src", "SampleProject"),
            SemanticGraph());

        var customerSymbol = index.Symbols.FirstOrDefault(s => s.Name == "Customer");
        customerSymbol.Should().NotBeNull();
        customerSymbol!.Namespace.Should().Be("SampleProject.Customers");
    }

    [Fact]
    public void Index_TamperedGraph_IsNotSemanticAuthority()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var valid = SemanticGraph();
        var tampered = new CSharpSymbolGraph
        {
            GraphHash = "sha256:" + new string('0', 64),
            LoadSucceeded = true,
            Nodes = valid.Nodes,
            Edges = valid.Edges
        };

        var index = _indexer.Index(
            Path.Combine(samplePath, "src", "SampleProject"),
            tampered);

        index.SemanticAuthority.Should().BeFalse();
        index.Symbols.Should().BeEmpty();
    }

    private static CSharpSymbolGraph SemanticGraph()
    {
        var graph = new CSharpSymbolGraph
        {
            LoadSucceeded = true,
            Nodes =
            [
                new()
                {
                    Id = "namespace",
                    Hash = "sha256:" + new string('b', 64),
                    Kind = "namespace",
                    Name = "Customers",
                    DisplayName = "SampleProject.Customers"
                },
                new()
                {
                    Id = "customer",
                    Hash = "sha256:" + new string('c', 64),
                    Kind = "type",
                    Name = "Customer",
                    DisplayName = "SampleProject.Customers.Customer",
                    TypeKind = "Class",
                    ContainingNodeId = "namespace",
                    FilePaths = ["Customers/Customer.cs"]
                },
                new()
                {
                    Id = "map",
                    Hash = "sha256:" + new string('d', 64),
                    Kind = "member",
                    MemberKind = "Method",
                    Name = "Map",
                    DisplayName = "SampleProject.Customers.Customer.Map()",
                    ContainingNodeId = "customer",
                    FilePaths = ["Customers/Customer.cs"]
                }
            ],
            Edges =
            [
                new()
                {
                    Id = "contains",
                    Hash = "sha256:" + new string('e', 64),
                    Kind = "contains",
                    FromNodeId = "customer",
                    ToNodeId = "map"
                }
            ]
        };
        return new CSharpSymbolGraph
        {
            GraphHash = CSharpSymbolGraphFingerprint.Create(graph),
            LoadSucceeded = graph.LoadSucceeded,
            Nodes = graph.Nodes,
            Edges = graph.Edges
        };
    }
}

public class ContextSelectorTests
{
    private readonly ContextSelector _selector = new();

    [Fact]
    public void Select_CustomerTask_SelectsCustomerFiles()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var indexer = new CodebaseIndexer();
        var index = indexer.Index(samplePath);

        var package = _selector.Select(
            index,
            "TASK-001",
            "Fix null handling in CustomerMapper",
            ["src/**"]);

        package.SelectedFiles.Should().Contain(f => f.Contains("CustomerMapper"));
        package.SelectedFiles.Should().Contain(f => f.Contains("Customer"));
        package.Id.Should().StartWith("CTX-");
        package.TaskId.Should().Be("TASK-001");
    }

    [Fact]
    public void Select_CustomerTask_IncludesRelatedTests()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var indexer = new CodebaseIndexer();
        var index = indexer.Index(samplePath);

        var package = _selector.Select(
            index,
            "TASK-001",
            "Fix null handling in CustomerMapper",
            ["src/**", "tests/**"]);

        package.RelevantTests.Should().Contain(t => t.Contains("CustomerMapper"));
    }

    [Fact]
    public void Select_EstimatesTokens()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var indexer = new CodebaseIndexer();
        var index = indexer.Index(samplePath);

        var package = _selector.Select(
            index,
            "TASK-001",
            "Fix null handling in CustomerMapper",
            ["src/**"]);

        package.EstimatedTokens.Should().BeGreaterThan(0);
    }
}

public class RepositoryContextCompilerTests
{
    private readonly RepositoryContextCompiler _compiler = new();

    [Fact]
    public void Compile_RespectsScopeAndExcludesForbiddenAndArtifactFiles()
    {
        using var repository = new TemporaryContextRepository();
        repository.Write("src/Allowed/OrderHandler.cs", "namespace Demo; public class OrderHandler { public void Handle() { } }");
        repository.Write("src/Forbidden/SecretHandler.cs", "namespace Demo; public class SecretHandler { }");
        repository.Write("tests/Allowed/OrderHandlerTests.cs", "namespace Demo.Tests; public class OrderHandlerTests { }");
        repository.Write("src/.env.cs", "const string Token = \"must-not-enter-context\";");
        repository.Write("bin/Generated.cs", "namespace Demo; public class Generated { }");
        repository.Write(".git/Internal.cs", "namespace Demo; public class Internal { }");

        var result = _compiler.Compile(
            repository.Path,
            Contract(
                objective: "Update OrderHandler and its existing tests",
                allowed: ["src/**", "tests/**"],
                forbidden: ["src/Forbidden/**"]),
            "baseline-123");

        result.CodeContext.Keys.Should().BeEquivalentTo(
            "src/Allowed/OrderHandler.cs",
            "tests/Allowed/OrderHandlerTests.cs");
        result.Prompt.Should().Contain("class OrderHandler");
        result.Prompt.Should().NotContain("Symbols:",
            "the textual fallback is not semantic authority");
        result.Manifest.SemanticIndex.Should().Be("textual-file-inventory");
        result.Prompt.Should().Contain("public void Handle()");
        result.Prompt.Should().NotContain("SecretHandler");
        result.Prompt.Should().NotContain("must-not-enter-context");
        result.Manifest.Selections.Should().Contain(selection =>
            selection.Path == "src/.env.cs" &&
            selection.Decision == "omitted" &&
            selection.Reason == "sensitive-path-filter");
        result.Manifest.Source.Should().Be("isolated-git-worktree");
        result.Manifest.Files.Should().OnlyContain(file =>
            file.Sha256.StartsWith("sha256:") &&
            file.IncludedSha256.StartsWith("sha256:"));
    }

    [Fact]
    public void Compile_EnforcesDeterministicTokenAndCharacterLimits()
    {
        using var repository = new TemporaryContextRepository();
        repository.Write(
            "src/Orders/OrderHandler.cs",
            "namespace Demo; public class OrderHandler { /*" + new string('x', 2_000) + "*/ }");
        repository.Write(
            "src/Orders/OrderCommand.cs",
            "namespace Demo; public record OrderCommand(string Value);" + new string('y', 800));
        var contract = Contract(
            objective: "Change OrderHandler and OrderCommand",
            allowed: ["src/**"]);
        var options = new ContextCompilationOptions
        {
            MaxTokens = 650,
            MaxCharacters = 1_000,
            MaxFileCharacters = 300
        };

        var first = _compiler.Compile(repository.Path, contract, "baseline-123", options);
        var second = _compiler.Compile(repository.Path, contract, "baseline-123", options);

        first.Prompt.Length.Should().BeLessThanOrEqualTo(1_000);
        first.Manifest.EstimatedTokens.Should().BeLessThanOrEqualTo(650);
        first.Manifest.Truncated.Should().BeTrue();
        first.Manifest.Files.Should().Contain(file => file.Truncated);
        first.Manifest.ManifestHash.Should().Be(second.Manifest.ManifestHash);
        first.Manifest.Id.Should().Be(second.Manifest.Id);
        first.CodeContext.Should().BeEquivalentTo(second.CodeContext);
    }

    [Fact]
    public void Compile_RejectsScopeTraversalBeforeReadingFiles()
    {
        using var repository = new TemporaryContextRepository();
        repository.Write("src/Allowed.cs", "namespace Demo; public class Allowed { }");
        var contract = Contract(
            objective: "Change Allowed",
            allowed: ["../outside/**"]);

        var action = () => _compiler.Compile(repository.Path, contract, "baseline-123");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot traverse directories*");
    }

    private static TaskContract Contract(
        string objective,
        List<string> allowed,
        List<string>? forbidden = null) => new()
        {
            Id = "CTX-TEST",
            Objective = objective,
            AcceptanceCriteria = ["Existing tests still pass"],
            Scope = new ScopeDefinition
            {
                Allowed = allowed,
                Forbidden = forbidden ?? []
            },
            Budget = ExecutionBudget.Default
        };

    private sealed class TemporaryContextRepository : IDisposable
    {
        public TemporaryContextRepository()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aecs-context-tests-{Guid.NewGuid():N}");
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
