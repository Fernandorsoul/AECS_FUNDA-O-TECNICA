using AECS.Application.Parsing;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class OrbisIntegrationContractTests
{
    [Fact]
    public void DocumentedYaml_ParsesWithTheRealStrictParser()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AECS.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the test must run with the repository documentation available");
        var document = File.ReadAllText(Path.Combine(directory!.FullName, "docs", "orbis-integration-interface.md"));
        var start = document.IndexOf("```yaml", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        start += "```yaml".Length;
        var end = document.IndexOf("```", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        var contract = new TaskContractParser().Parse(document[start..end]);
        contract.Id.Should().Be("TASK-001");
        contract.Execution.Target.Should().Be("MyApp.slnx");
        contract.AcceptanceRequirements.Should().ContainSingle()
            .Which.Evidence.Reference.Should().Be("Tests");
    }
}
