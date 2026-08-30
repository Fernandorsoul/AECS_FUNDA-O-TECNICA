using AECS.Application.ContextCompiler;
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
    }

    [Fact]
    public void Index_SampleProject_FindsSymbols()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var index = _indexer.Index(Path.Combine(samplePath, "src", "SampleProject"));

        index.Symbols.Should().NotBeEmpty();
        index.Symbols.Should().Contain(s => s.Name == "Customer");
        index.Symbols.Should().Contain(s => s.Name == "CustomerMapper");
        index.Symbols.Should().Contain(s => s.Name == "CustomerService");
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

        var index = _indexer.Index(Path.Combine(samplePath, "src", "SampleProject"));

        var customerSymbol = index.Symbols.FirstOrDefault(s => s.Name == "Customer");
        customerSymbol.Should().NotBeNull();
        customerSymbol!.Namespace.Should().Be("SampleProject.Customers");
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
        var index = indexer.Index(Path.Combine(samplePath, "src", "SampleProject"));

        var package = _selector.Select(
            index,
            "TASK-001",
            "Fix null handling in CustomerMapper",
            ["src/Customers/**"]);

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
        var srcIndex = indexer.Index(Path.Combine(samplePath, "src", "SampleProject"));
        var testIndex = indexer.Index(Path.Combine(samplePath, "tests", "SampleProject.Tests"));

        // Merge indexes
        var mergedIndex = new CodebaseIndex
        {
            RootPath = samplePath,
            SourceFiles = srcIndex.SourceFiles,
            TestFiles = testIndex.TestFiles,
            Symbols = srcIndex.Symbols.Concat(testIndex.Symbols).ToList(),
            Dependencies = srcIndex.Dependencies.Concat(testIndex.Dependencies)
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        var package = _selector.Select(
            mergedIndex,
            "TASK-001",
            "Fix null handling in CustomerMapper",
            ["src/Customers/**"]);

        package.RelevantTests.Should().Contain(t => t.Contains("CustomerMapper"));
    }

    [Fact]
    public void Select_EstimatesTokens()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample", "SampleProject"));

        if (!Directory.Exists(samplePath))
            return;

        var indexer = new CodebaseIndexer();
        var index = indexer.Index(Path.Combine(samplePath, "src", "SampleProject"));

        var package = _selector.Select(
            index,
            "TASK-001",
            "Fix null handling in CustomerMapper",
            ["src/Customers/**"]);

        package.EstimatedTokens.Should().BeGreaterThan(0);
    }
}
