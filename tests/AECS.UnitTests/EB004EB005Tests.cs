using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class EB004MissingChangeVerifierTests
{
    [Fact]
    public void ChangedContractWithUnchangedResolvedImplementation_IsReported()
    {
        var contract = Type("contract", "IService", "src/App/Changed.cs", "Interface");
        var implementation = Type("implementation", "Service", "src/App/Service.cs", "Class");
        var relation = SemanticGraphFixture.Edge("implements", implementation.Id, contract.Id);
        var input = SemanticGraphFixture.Input(
            [contract, implementation],
            [contract, implementation],
            baselineEdges: [relation],
            candidateEdges: [relation],
            changedFiles: ["src/App/Changed.cs"]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should()
            .ContainSingle(finding => finding.RuleId == "EB004-MISSING-IMPLEMENTATION");
    }

    [Fact]
    public void ChangedContractAndImplementation_PassTogether()
    {
        var contract = Type("contract", "IService", "src/App/Changed.cs", "Interface");
        var implementation = Type("implementation", "Service", "src/App/Service.cs", "Class");
        var relation = SemanticGraphFixture.Edge("implements", implementation.Id, contract.Id);
        var input = SemanticGraphFixture.Input(
            [contract, implementation],
            [contract, implementation],
            baselineEdges: [relation],
            candidateEdges: [relation],
            changedFiles: ["src/App/Changed.cs", "src/App/Service.cs"]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should().BeEmpty();
    }

    [Fact]
    public void UnchangedTestWithResolvedReferenceToImpact_IsReported()
    {
        var production = Type("production", "Service", "src/App/Changed.cs", "Class");
        var test = SemanticGraphFixture.Node(
            "test", "member", "ShouldWork", "ServiceTests.ShouldWork()",
            project: "tests/App.Tests/App.Tests.csproj", file: "tests/App.Tests/ServiceTests.cs",
            memberKind: "Method", documentationId: "M:ServiceTests.ShouldWork");
        var relation = SemanticGraphFixture.Edge("references", test.Id, production.Id);
        var projects = new[]
        {
            SemanticGraphFixture.Project("src/App/App.csproj"),
            SemanticGraphFixture.Project("tests/App.Tests/App.Tests.csproj", test: true)
        };
        var input = SemanticGraphFixture.Input(
            [production, test],
            [production, test],
            baselineEdges: [relation],
            candidateEdges: [relation],
            changedFiles: ["src/App/Changed.cs"],
            projects: projects);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should()
            .ContainSingle(finding => finding.RuleId == "EB004-MISSING-TEST");
    }

    [Fact]
    public void TestLikeFileWithoutResolvedRelation_IsNotReported()
    {
        var production = Type("production", "Service", "src/App/Changed.cs", "Class");
        var unrelated = SemanticGraphFixture.Node(
            "test", "member", "ShouldWork", "ServiceTests.ShouldWork()",
            project: "tests/App.Tests/App.Tests.csproj", file: "tests/App.Tests/ServiceTests.cs",
            memberKind: "Method");
        var input = SemanticGraphFixture.Input(
            [production, unrelated],
            [production, unrelated],
            changedFiles: ["src/App/Changed.cs"],
            projects:
            [
                SemanticGraphFixture.Project("src/App/App.csproj"),
                SemanticGraphFixture.Project("tests/App.Tests/App.Tests.csproj", test: true)
            ]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should().BeEmpty();
    }

    [Fact]
    public void EntityResolvedThroughDbContextRequiresRelatedMigrationChange()
    {
        var context = Type("context", "AppDbContext", "src/App/AppDbContext.cs", "Class");
        var set = SemanticGraphFixture.Node(
            "set", "member", "Orders", "AppDbContext.Orders",
            file: "src/App/AppDbContext.cs", containing: context.Id, memberKind: "Property");
        var entity = Type("entity", "Order", "src/App/Changed.cs", "Class");
        var migration = Type("migration", "Initial", "src/App/Migrations/Initial.cs", "Class");
        var dbContext = SemanticGraphFixture.Node(
            "dbcontext", "type", "DbContext", "Microsoft.EntityFrameworkCore.DbContext",
            file: null, typeKind: "Class", external: true);
        var migrationBase = SemanticGraphFixture.Node(
            "migration-base", "type", "Migration", "Microsoft.EntityFrameworkCore.Migrations.Migration",
            file: null, typeKind: "Class", external: true);
        var edges = new[]
        {
            SemanticGraphFixture.Edge("inherits", context.Id, dbContext.Id),
            SemanticGraphFixture.Edge("references", set.Id, entity.Id),
            SemanticGraphFixture.Edge("inherits", migration.Id, migrationBase.Id)
        };
        var input = SemanticGraphFixture.Input(
            [context, set, entity, migration, dbContext, migrationBase],
            [context, set, entity, migration, dbContext, migrationBase],
            baselineEdges: edges,
            candidateEdges: edges,
            changedFiles: ["src/App/Changed.cs"]);

        new EB004MissingChangeVerifier().Verify(input).MissingChanges.Should()
            .ContainSingle(finding => finding.RuleId == "EB004-MISSING-MIGRATION");
    }

    private static AECS.Domain.Models.CSharpSymbolGraphNode Type(
        string id,
        string name,
        string file,
        string typeKind) => SemanticGraphFixture.Node(
            id, "type", name, $"App.{name}", file: file, typeKind: typeKind,
            documentationId: $"T:App.{name}");
}

public sealed class EB005HistoricalConflictVerifierTests
{
    [Fact]
    public void ProhibitedAdrPattern_IsReportedAsHeuristic()
    {
        var root = CreateRepository("public class Agent { private LLMOnly? _mode; }");
        try
        {
            var result = new EB005HistoricalConflictVerifier().Verify(
                root,
                [new HistoricalDecision
                {
                    Id = "ADR-1",
                    Source = "ADR-1",
                    Type = DecisionType.Adr,
                    Description = "No LLM-only enforcement",
                    ProhibitedPatterns = ["LLMOnly"]
                }]);

            result.Conflicts.Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnrelatedCode_HasNoHistoricalConflict()
    {
        var root = CreateRepository("public class Agent { }");
        try
        {
            var result = new EB005HistoricalConflictVerifier().Verify(
                root,
                [new HistoricalDecision
                {
                    Id = "ADR-1",
                    Source = "ADR-1",
                    Type = DecisionType.Adr,
                    Description = "No LLM-only enforcement",
                    ProhibitedPatterns = ["LLMOnly"]
                }]);

            result.Conflicts.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRepository(string code)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aecs-eb005-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Agent.cs"), code);
        return root;
    }
}
