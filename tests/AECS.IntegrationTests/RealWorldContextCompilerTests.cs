using AECS.Application.ContextCompiler;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class RealWorldContextCompilerTests
{
    [Fact]
    public void VersionedAgroContracts_ParseAndAgro003HasConsistentScope()
    {
        var tasksPath = Path.Combine(
            RealWorldFixtureRepository.FixturePath,
            "repository",
            "tasks");
        var parser = new TaskContractParser();
        var contracts = Directory.EnumerateFiles(tasksPath, "agro-*.yaml")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(parser.ParseFromFile)
            .ToList();

        contracts.Select(contract => contract.Id).Should().Equal(
            "AGRO-001",
            "AGRO-002",
            "AGRO-003");
        contracts.Should().OnlyContain(contract =>
            contract.Execution.WorkingDirectory == "Backend" &&
            contract.Execution.Target == "AgronomoPlus.Tests/AgronomoPlus.Tests.csproj");

        var agro003 = contracts.Single(contract => contract.Id == "AGRO-003");
        agro003.Scope.Allowed.Should().Contain(
            "Backend/AgronomoPlus.Application/Services/PersonService.cs");
        agro003.Scope.Allowed.Should().Contain(
            "Backend/AgronomoPlus.Application/Services/KeycloakAdminService.cs");
        agro003.Scope.Allowed.Should().NotContain(path =>
            path.StartsWith(
                "Backend/AgronomoPlus.Infrastructure/",
                StringComparison.OrdinalIgnoreCase));
        agro003.Scope.Forbidden.Should().Contain(
            "Backend/AgronomoPlus.Infrastructure/**");
    }

    [Fact]
    public async Task Agro001_ReceivesExistingHandlerCommandModelAndTests_FromDetachedWorktree()
    {
        await using var repository = await RealWorldFixtureRepository.CreateAsync();
        var repositoryPath = repository.Path;
        var taskPath = Path.Combine(
            repositoryPath,
            "tasks",
            "agro-001-animal-validation.yaml");
        var manager = new GitWorkspaceManager(repository.ProcessRunner);
        var baseline = await manager.CaptureBaselineAsync(
            repositoryPath,
            CancellationToken.None);
        await using var workspace = await manager.CreateWorkspaceAsync(
            baseline,
            CancellationToken.None);
        var contract = new TaskContractParser().ParseFromFile(taskPath);

        var result = new RepositoryContextCompiler().Compile(
            workspace.Path,
            contract,
            baseline.Commit);

        workspace.Path.Should().NotBe(repositoryPath);
        result.Manifest.Source.Should().Be("isolated-git-worktree");
        result.CodeContext.Should().ContainKey(
            "Backend/AgronomoPlus.Application/Modules/Animal/Application/Commands/CreateAnimal/CreateAnimalHandler.cs");
        result.CodeContext.Should().ContainKey(
            "Backend/AgronomoPlus.Application/Modules/Animal/Application/Commands/CreateAnimal/CreateAnimalCommand.cs");
        result.CodeContext.Should().ContainKey(
            "Backend/AgronomoPlus.Domain/Models/Animal.cs");
        result.CodeContext.Should().ContainKey(
            "Backend/AgronomoPlus.Tests/Handlers/Animal/AnimalHandlerTests.cs");
        result.CodeContext.Keys.Should().NotContain(path =>
            path.StartsWith("Backend/AgronomoPlus.Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Backend/AgronomoPlus.Api/", StringComparison.OrdinalIgnoreCase));
        result.Prompt.Should().Contain("CreateAnimalHandler");
        result.Prompt.Should().Contain("CreateAnimalCommand");
        result.Prompt.Should().Contain("AgronomoPlus.Domain.Models.Animal");
        result.Prompt.Should().Contain("CreateAnimalHandlerTests");
        result.Manifest.Files.Should().OnlyContain(file =>
            file.Sha256.StartsWith("sha256:") && file.IncludedSha256.StartsWith("sha256:"));

        (await repository.WorktreesAsync()).Should().HaveCount(2);
        (await repository.SnapshotAsync()).Status.Should().BeEmpty();
    }
}
