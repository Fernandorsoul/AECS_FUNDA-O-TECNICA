using System.Text.Json;
using AECS.Application.Parsing;
using AECS.Application.RepositorySnapshots;
using AECS.Application.Staging;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class RepositorySnapshotTests
{
    [Fact]
    public async Task SameBaselineAndConfiguration_ProducesIdenticalContentAddressedInventory()
    {
        await using var fixture = await SnapshotFixture.CreateAsync();
        var builder = new RepositorySnapshotBuilder(fixture.Runner);
        var contract = fixture.Contract();

        var first = await builder.BuildAsync(
            fixture.Path,
            await fixture.HeadAsync(),
            contract,
            ToolCommands(),
            CancellationToken.None);
        var second = await builder.BuildAsync(
            fixture.Path,
            await fixture.HeadAsync(),
            contract,
            ToolCommands(),
            CancellationToken.None);

        first.SnapshotHash.Should().Be(second.SnapshotHash).And.StartWith("sha256:");
        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(second));
        JsonSerializer.Serialize(first).Should().NotContain("not-persisted")
            .And.NotContain("../outside");
        first.SchemaVersion.Should().Be(RepositorySnapshotSchema.SnapshotVersion);
        first.StrategyVersion.Should().Be(RepositorySnapshotSchema.DiscoveryStrategy);
        first.ConfigurationHash.Should().StartWith("sha256:");
        first.Files.Should().OnlyContain(file => file.Hash.StartsWith("git:"));
        first.Files.Should().NotContain(file =>
            file.Path.StartsWith("vendor/") ||
            file.Path.StartsWith("bin/") ||
            file.Path == "release/app.dll" ||
            file.Path == ".env" ||
            file.Path == "unsafe-link");
        first.ExcludedEntryCount.Should().BeGreaterThanOrEqualTo(4);
        first.Solutions.Should().ContainSingle(solution => solution.Path == "eng/Repo.slnx");
        first.Projects.Should().HaveCount(4);
        first.Projects.Should().Contain(project =>
            project.Path == "tests/Unit/App.UnitTests.csproj" &&
            project.IsTestProject &&
            project.Frameworks.Contains("net8.0"));
        first.Projects.Should().Contain(project =>
            project.Path == "tests/Integration/App.IntegrationTests.csproj" &&
            project.IsTestProject);
        first.Languages.Should().Contain(language => language.Name == "C#" && language.FileCount >= 4);
        first.Frameworks.Should().Contain("net8.0");
        first.Manifests.Should().Contain(manifest => manifest.Path == "package.json");
        first.Packages.Should().Contain(package =>
            package.Ecosystem == "nuget" && package.Name == "MediatR");
        first.Packages.Should().Contain(package =>
            package.Ecosystem == "npm" && package.Name == "left-pad");
        first.Entrypoints.Should().Contain(entrypoint =>
            entrypoint.Path == "src/App/Program.cs" &&
            entrypoint.ProjectPath == "src/App/App.csproj");
        first.TestSuites.Should().Contain(suite =>
            suite.Category == "Unit" &&
            suite.Target == "tests/Unit/App.UnitTests.csproj" &&
            suite.Mode == "Required");
        first.TestSuites.Should().Contain(suite =>
            suite.Category == "Integration" &&
            suite.Target == "tests/Integration/App.IntegrationTests.csproj" &&
            suite.Mode == "Optional");
        first.Relationships.Should().Contain(relation =>
            relation.Kind == "project-reference" &&
            relation.From == "src/App/App.csproj" &&
            relation.To == "src/Core/Core.csproj");
        first.Relationships.Count(relation => relation.Kind == "solution-project")
            .Should().Be(4);
        first.Tools.Should().Contain(tool =>
            tool.Name == "git" && tool.Available && tool.Version == "git version fixture");
    }

    [Fact]
    public async Task Compare_DetectsAddedRemovedAndChangedFiles_WhileIgnoringExcludedContent()
    {
        await using var fixture = await SnapshotFixture.CreateAsync();
        var builder = new RepositorySnapshotBuilder(fixture.Runner);
        var contract = fixture.Contract();
        var before = await builder.BuildAsync(
            fixture.Path,
            await fixture.HeadAsync(),
            contract,
            ToolCommands(),
            CancellationToken.None);

        await fixture.ChangeInventoryAsync();
        var after = await builder.BuildAsync(
            fixture.Path,
            await fixture.HeadAsync(),
            contract,
            ToolCommands(),
            CancellationToken.None);
        var difference = RepositorySnapshotComparer.Compare(before, after);

        difference.AddedFiles.Should().Equal("src/Core/NewType.cs");
        difference.RemovedFiles.Should().Equal("src/Core/CoreType.cs");
        difference.ChangedFiles.Should().Equal("src/App/Program.cs");
        difference.HasChanges.Should().BeTrue();

        await fixture.ChangeOnlyExcludedContentAsync();
        var excludedOnly = await builder.BuildAsync(
            fixture.Path,
            await fixture.HeadAsync(),
            contract,
            ToolCommands(),
            CancellationToken.None);

        excludedOnly.SnapshotHash.Should().Be(after.SnapshotHash,
            "configured excluded directories do not participate in the inventory address");
        RepositorySnapshotComparer.Compare(after, excludedOnly).HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task RealWorldMonorepo_InventoryFindsSubdirectorySolutionAndMultipleProjects()
    {
        await using var repository = await RealWorldFixtureRepository.CreateAsync();
        var manager = new GitWorkspaceManager(repository.ProcessRunner);
        var baseline = await manager.CaptureBaselineAsync(repository.Path, CancellationToken.None);
        await using var workspace = await manager.CreateWorkspaceAsync(
            baseline,
            CancellationToken.None);
        var contract = new TaskContractParser().ParseFromFile(Path.Combine(
            workspace.Path,
            "tasks",
            "agro-001-animal-validation.yaml"));

        var snapshot = await new RepositorySnapshotBuilder(repository.ProcessRunner).BuildAsync(
            workspace.Path,
            baseline.Commit,
            contract,
            ToolCommands(),
            CancellationToken.None);

        snapshot.Solutions.Should().ContainSingle(solution =>
            solution.Path == "Backend/AgronomoPlus.slnx" && solution.Projects.Count == 3);
        snapshot.Projects.Should().HaveCount(3).And.OnlyContain(project =>
            project.Path.StartsWith("Backend/", StringComparison.Ordinal));
        snapshot.Projects.Should().Contain(project =>
            project.Path == "Backend/AgronomoPlus.Tests/AgronomoPlus.Tests.csproj" &&
            project.IsTestProject);
        snapshot.Relationships.Count(relation => relation.Kind == "solution-project")
            .Should().Be(3);
        snapshot.Relationships.Should().Contain(relation =>
            relation.Kind == "project-reference" &&
            relation.From == "Backend/AgronomoPlus.Application/AgronomoPlus.Application.csproj" &&
            relation.To == "Backend/AgronomoPlus.Domain/AgronomoPlus.Domain.csproj");
        snapshot.Frameworks.Should().Contain("net9.0");
        snapshot.TestSuites.Should().Contain(suite =>
            suite.Name == "Tests" &&
            suite.Target == "Backend/AgronomoPlus.Tests/AgronomoPlus.Tests.csproj");
    }

    private static List<ExecutionCommandEvidence> ToolCommands() =>
    [
        new()
        {
            FileName = "git",
            Arguments = ["--version"],
            ExitCode = 0,
            StandardOutput = "git version fixture\n"
        },
        new()
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            ExitCode = 0,
            StandardOutput = "9.0.100\n"
        }
    ];

    private sealed class SnapshotFixture : IAsyncDisposable
    {
        private const string RootPrefix = "aecs-repository-snapshot-tests-";

        private SnapshotFixture(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public SystemProcessRunner Runner { get; } = new();

        public static async Task<SnapshotFixture> CreateAsync()
        {
            var fixture = new SnapshotFixture(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{RootPrefix}{Guid.NewGuid():N}"));
            Directory.CreateDirectory(fixture.Path);
            await fixture.WriteAsync("src/Core/Core.csproj", Project());
            await fixture.WriteAsync("src/Core/CoreType.cs", "namespace Core; public sealed class CoreType { }\n");
            await fixture.WriteAsync("src/App/App.csproj", Project(
                outputType: "Exe",
                references: ["../Core/Core.csproj"],
                packages: [("MediatR", "12.2.0")]));
            await fixture.WriteAsync("src/App/Program.cs", "Console.WriteLine(\"baseline\");\n");
            await fixture.WriteAsync("tests/Unit/App.UnitTests.csproj", Project(
                isTest: true,
                references: ["../../src/App/App.csproj"],
                packages: [("xunit", "2.9.3"), ("Microsoft.NET.Test.Sdk", "17.11.1")]));
            await fixture.WriteAsync("tests/Unit/AppTests.cs", "public sealed class AppTests { }\n");
            await fixture.WriteAsync("tests/Integration/App.IntegrationTests.csproj", Project(
                isTest: true,
                references: ["../../src/App/App.csproj"],
                packages: [("MSTest.TestFramework", "3.6.0")]));
            await fixture.WriteAsync("tests/Integration/AppIntegrationTests.cs",
                "public sealed class AppIntegrationTests { }\n");
            await fixture.WriteAsync("eng/Repo.slnx", """
                <Solution>
                  <Project Path="../src/Core/Core.csproj" />
                  <Project Path="../src/App/App.csproj" />
                  <Project Path="../tests/Unit/App.UnitTests.csproj" />
                  <Project Path="../tests/Integration/App.IntegrationTests.csproj" />
                </Solution>
                """);
            await fixture.WriteAsync("global.json", "{ \"sdk\": { \"version\": \"9.0.100\" } }\n");
            await fixture.WriteAsync("package.json", """
                {
                  "name": "snapshot-fixture",
                  "dependencies": { "left-pad": "1.3.0" }
                }
                """);
            await fixture.WriteAsync("Directory.Packages.props", """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="FluentAssertions" Version="8.10.0" />
                  </ItemGroup>
                </Project>
                """);
            await fixture.WriteAsync("vendor/ignored.txt", "ignored\n");
            await fixture.WriteAsync("bin/generated.dll", "artifact\n");
            await fixture.WriteAsync("release/app.dll", "artifact\n");
            await fixture.WriteAsync(".env", "PASSWORD=not-persisted\n");
            await fixture.GitAsync("init", "--initial-branch=fixture");
            await fixture.GitAsync("config", "user.email", "aecs-snapshot@example.invalid");
            await fixture.GitAsync("config", "user.name", "AECS Snapshot Tests");
            await fixture.GitAsync("config", "core.autocrlf", "false");
            await fixture.GitAsync("add", "-A", "--");
            var linkSource = System.IO.Path.Combine(fixture.Path, "link-source.tmp");
            await File.WriteAllTextAsync(linkSource, "../outside\n");
            var linkObject = (await fixture.GitAsync("hash-object", "-w", "link-source.tmp"))
                .StandardOutput.Trim();
            File.Delete(linkSource);
            await fixture.GitAsync(
                "update-index", "--add", "--cacheinfo", $"120000,{linkObject},unsafe-link");
            await fixture.GitAsync("commit", "-m", "snapshot baseline");
            return fixture;
        }

        public Task<string> HeadAsync() => GitTextAsync("rev-parse", "HEAD");

        public TaskContract Contract() => new()
        {
            Id = "TASK-REPOSITORY-SNAPSHOT",
            Objective = "Inventory a deterministic monorepo baseline",
            Execution = new RepositoryExecutionProfile
            {
                Target = "eng/Repo.slnx",
                RepositorySnapshot = new RepositorySnapshotProfile
                {
                    ExcludedDirectories = ["vendor"]
                },
                TestSuites = new TestSuiteMatrix
                {
                    Unit = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Required,
                        Target = "tests/Unit/App.UnitTests.csproj"
                    },
                    Integration = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Optional,
                        Target = "tests/Integration/App.IntegrationTests.csproj"
                    }
                }
            }
        };

        public async Task ChangeInventoryAsync()
        {
            await WriteAsync("src/App/Program.cs", "Console.WriteLine(\"changed\");\n");
            File.Delete(System.IO.Path.Combine(Path, "src", "Core", "CoreType.cs"));
            await WriteAsync("src/Core/NewType.cs", "namespace Core; public sealed class NewType { }\n");
            await GitAsync("add", "-A", "--");
            await GitAsync("commit", "-m", "change inventory");
        }

        public async Task ChangeOnlyExcludedContentAsync()
        {
            await WriteAsync("vendor/another-ignored.txt", "still ignored\n");
            await GitAsync("add", "-A", "--");
            await GitAsync("commit", "-m", "change excluded content");
        }

        public async ValueTask DisposeAsync()
        {
            var root = System.IO.Path.GetFullPath(Path);
            var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
                .TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!root.StartsWith(
                    temporaryRoot + System.IO.Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(root).StartsWith(RootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to delete unexpected path: {root}");
            }
            if (!Directory.Exists(root))
                return;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }

        private async Task WriteAsync(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        private async Task<string> GitTextAsync(params string[] arguments) =>
            (await GitAsync(arguments)).StandardOutput.Trim();

        private async Task<ProcessExecutionResult> GitAsync(params string[] arguments)
        {
            var result = await Runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = Path,
                Timeout = TimeSpan.FromSeconds(30)
            }, CancellationToken.None);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
            }
            return result;
        }

        private static string Project(
            string? outputType = null,
            bool isTest = false,
            IReadOnlyList<string>? references = null,
            IReadOnlyList<(string Name, string Version)>? packages = null)
        {
            var properties = $"<TargetFramework>net8.0</TargetFramework>" +
                (outputType is null ? string.Empty : $"<OutputType>{outputType}</OutputType>") +
                (isTest ? "<IsTestProject>true</IsTestProject>" : string.Empty);
            var projectReferences = string.Join(string.Empty, (references ?? [])
                .Select(reference => $"<ProjectReference Include=\"{reference}\" />"));
            var packageReferences = string.Join(string.Empty, (packages ?? [])
                .Select(package =>
                    $"<PackageReference Include=\"{package.Name}\" Version=\"{package.Version}\" />"));
            return $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup>" +
                $"<ItemGroup>{projectReferences}{packageReferences}</ItemGroup></Project>";
        }
    }
}
