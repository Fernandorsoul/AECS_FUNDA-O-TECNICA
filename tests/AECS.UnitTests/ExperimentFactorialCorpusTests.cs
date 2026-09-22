using AECS.Application.Experiments;
using AECS.Application.Parsing;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

/// <summary>
/// Static validation of the 20-task factorial corpus fixture: every dataset
/// parses as a valid v5 factorial protocol and every contract resolves and
/// parses with a matching id (plan §6 corpus gate, exercised in CI).
/// </summary>
public sealed class ExperimentFactorialCorpusTests
{
    private static string CorpusRoot => Path.GetFullPath(Path.Combine(
        FindRepositoryRoot(),
        "tests", "fixtures", "experiment-factorial-corpus"));

    [Theory]
    [InlineData("dataset.json", 20)]
    [InlineData("dataset.cloud.json", 20)]
    [InlineData("dataset.local.json", 20)]
    [InlineData("dataset.smoke.json", 2)]
    public void Loader_AcceptsCorpusDataset_WithExpectedTaskCount(string fileName, int taskCount)
    {
        var loaded = ExperimentDatasetLoader.Load(Path.Combine(CorpusRoot, fileName));

        loaded.Manifest.SchemaVersion.Should()
            .Be(ExperimentDatasetSchema.FactorialLedgerContextAbVersion);
        loaded.Manifest.Protocol!.Design.Should()
            .Be(ExperimentDesigns.FactorialLedgerContext2x2);
        loaded.Manifest.Variants.Should().HaveCount(4);
        loaded.Manifest.ReferenceVariantId.Should().Be("A");
        loaded.Manifest.Variants.Select(variant => variant.Id).Should()
            .BeEquivalentTo(["A", "B", "C", "D"]);
        loaded.Manifest.Tasks.Should().HaveCount(taskCount);
        var ledgerByArm = loaded.Manifest.Variants.ToDictionary(
            variant => variant.Id,
            variant => variant.ConstraintLedgerEnabled);
        ledgerByArm.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["A"] = false,
            ["B"] = true,
            ["C"] = false,
            ["D"] = true
        });
        loaded.Manifest.Variants.Count(variant =>
            variant.ContextStrategy == "naive-path-order").Should().Be(2);
        loaded.Manifest.Variants.Count(variant =>
            variant.ContextStrategy == "graph-ranked").Should().Be(2);
    }

    [Fact]
    public void AllCorpusContracts_ParseWithMatchingIds()
    {
        var loaded = ExperimentDatasetLoader.Load(Path.Combine(CorpusRoot, "dataset.json"));
        var parser = new TaskContractParser();
        var parsed = new List<(string TaskId, string ContractId, bool Build, bool Tests)>();

        foreach (var task in loaded.Manifest.Tasks)
        {
            var contractPath = loaded.ContractPath(task);
            File.Exists(contractPath).Should().BeTrue($"contract missing: {task.ContractPath}");
            var contract = parser.ParseFromFile(contractPath);
            contract.Id.Should().Be(task.Id,
                "dataset task id must match the contract task id");
            contract.Verification.Build.Should().BeTrue(
                $"{task.Id} must require build (integration corpus)");
            contract.Verification.UnitTests.Should().BeTrue(
                $"{task.Id} must require unit tests (integration corpus)");
            parsed.Add((task.Id, contract.Id, contract.Verification.Build,
                contract.Verification.UnitTests));
        }

        parsed.Should().HaveCount(20);
        parsed.Select(item => item.ContractId).Should().OnlyHaveUniqueItems();
        parsed.Select(item => item.ContractId).Should().Contain("EXP-C01");
        parsed.Select(item => item.ContractId).Should().Contain("EXP-C20");
    }

    [Fact]
    public void AllCorpusContracts_HaveHostRuntimeAndRealProjectTarget()
    {
        var tasksDir = Path.Combine(CorpusRoot, "repository", "tasks");
        var yamlFiles = Directory.GetFiles(tasksDir, "EXP-C*.yaml");
        yamlFiles.Should().HaveCount(20);

        var parser = new TaskContractParser();
        foreach (var path in yamlFiles)
        {
            var contract = parser.ParseFromFile(path);
            contract.Execution.Target.Should().Be("RealProject.slnx");
            contract.Execution.Runtime.Should().Be(
                RepositoryExecutionProfile.HostRuntime);
            contract.Verification.Scope.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("dataset.json", 4)]
    [InlineData("dataset.cloud.json", 4)]
    [InlineData("dataset.local.json", 4)]
    public void CalibratedLoader_AcceptsFactorialDataset(string fileName, int taskCount)
    {
        var root = Path.Combine(
            FindRepositoryRoot(), "tests", "fixtures", "experiment-factorial-calibrated");
        var loaded = ExperimentDatasetLoader.Load(Path.Combine(root, fileName));

        loaded.Manifest.SchemaVersion.Should()
            .Be(ExperimentDatasetSchema.FactorialLedgerContextAbVersion);
        loaded.Manifest.Variants.Should().HaveCount(4);
        loaded.Manifest.Tasks.Should().HaveCount(taskCount);

        var parser = new TaskContractParser();
        foreach (var task in loaded.Manifest.Tasks)
        {
            var contract = parser.ParseFromFile(loaded.ContractPath(task));
            contract.Id.Should().Be(task.Id);
            contract.Verification.Build.Should().BeTrue();
            contract.Verification.UnitTests.Should().BeTrue();
            contract.Execution.Target.Should().Be("RealProject.slnx");
        }
    }

    [Fact]
    public void CalibratedFixture_HasFourContractsOnDisk()
    {
        var tasksDir = Path.Combine(
            FindRepositoryRoot(), "tests", "fixtures",
            "experiment-factorial-calibrated", "repository", "tasks");
        Directory.GetFiles(tasksDir, "EXP-K*.yaml").Should().HaveCount(4);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AECS.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
