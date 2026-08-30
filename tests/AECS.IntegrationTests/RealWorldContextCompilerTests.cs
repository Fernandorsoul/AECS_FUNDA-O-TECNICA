using AECS.Application.ContextCompiler;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class RealWorldContextCompilerTests
{
    [Fact]
    public async Task Agro001_ReceivesExistingHandlerCommandModelAndTests_FromDetachedWorktree()
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var repositoryPath = Path.Combine(solutionRoot, "real-world-demo");
        var taskPath = Path.Combine(
            repositoryPath,
            "tasks",
            "agro-001-animal-validation.yaml");
        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")) || !File.Exists(taskPath))
            return; // The real repository is an opt-in local fixture and is not committed to AECS.

        var processRunner = new SystemProcessRunner();
        var commit = (await GitAsync(processRunner, repositoryPath, "rev-parse", "HEAD"))
            .StandardOutput.Trim();
        var branch = (await GitAsync(
                processRunner,
                repositoryPath,
                "rev-parse",
                "--abbrev-ref",
                "HEAD"))
            .StandardOutput.Trim();
        var baseline = new BaselineSnapshot
        {
            Commit = commit,
            Branch = branch,
            GitStatus = string.Empty,
            RepositoryPath = repositoryPath
        };
        var manager = new GitWorkspaceManager(processRunner);
        await using var workspace = await manager.CreateWorkspaceAsync(
            baseline,
            CancellationToken.None);
        var contract = new TaskContractParser().ParseFromFile(taskPath);

        var result = new RepositoryContextCompiler().Compile(
            workspace.Path,
            contract,
            commit);

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
    }

    private static async Task<ProcessExecutionResult> GitAsync(
        SystemProcessRunner processRunner,
        string repositoryPath,
        params string[] arguments)
    {
        var result = await processRunner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repositoryPath,
            Timeout = TimeSpan.FromSeconds(30)
        }, CancellationToken.None);
        result.Succeeded.Should().BeTrue(
            $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result;
    }
}
