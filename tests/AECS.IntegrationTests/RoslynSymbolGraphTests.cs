using System.Text.Json;
using AECS.Application.ContextCompiler;
using AECS.Application.Parsing;
using AECS.Application.RepositorySnapshots;
using AECS.Application.Staging;
using AECS.Application.SymbolGraphs;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using FluentAssertions;

namespace AECS.IntegrationTests;

[Collection(RoslynMsBuildCollection.Name)]
public sealed class RoslynSymbolGraphTests
{
    [Fact]
    public async Task SemanticGraph_ResolvesStableSymbolsRelationsAndProjectReferences()
    {
        await using var fixture = await SymbolGraphFixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync();
        var builder = new RoslynSymbolGraphBuilder();

        var first = await builder.BuildAsync(fixture.Path, snapshot, cancellationToken: default);
        var second = await builder.BuildAsync(fixture.Path, snapshot, cancellationToken: default);

        first.LoadSucceeded.Should().BeTrue(string.Join("; ", first.Diagnostics.Select(item => item.Message)));
        first.GraphHash.Should().Be(second.GraphHash).And.StartWith("sha256:");
        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(second));
        first.RepositorySnapshotHash.Should().Be(snapshot.SnapshotHash);
        first.CompilerVersion.Should().NotBeNullOrWhiteSpace();
        first.MsBuildVersion.Should().NotBeNullOrWhiteSpace();
        first.GlobalProperties.Should().Contain(option =>
            option.Name == "Configuration" && option.Value == "Release");
        first.Projects.Should().HaveCount(2).And.OnlyContain(project =>
            project.Hash.StartsWith("sha256:") &&
            project.LanguageVersion.Length > 0 &&
            project.OutputKind.Length > 0);
        first.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Source == "compiler" && diagnostic.Code == "CS0246",
            "missing semantic references must remain explicit in the graph");
        first.Nodes.Should().Contain(node => node.Kind == "project");
        first.Nodes.Should().Contain(node => node.Kind == "file");
        first.Nodes.Should().Contain(node => node.Kind == "namespace");
        first.Nodes.Should().Contain(node => node.Kind == "type");
        first.Nodes.Should().Contain(node => node.Kind == "member");
        first.Nodes.Should().OnlyContain(node =>
            node.Id.StartsWith("CSN-") && node.Hash.StartsWith("sha256:"));
        first.Edges.Should().OnlyContain(edge =>
            edge.Id.StartsWith("CSE-") && edge.Hash.StartsWith("sha256:"));

        first.Nodes.Where(node => node.Kind == "member" && node.Name == "Handle" &&
                node.DisplayName.StartsWith("Demo.Core.PartialEntity.", StringComparison.Ordinal))
            .Should().HaveCount(2)
            .And.OnlyContain(node => !string.IsNullOrWhiteSpace(node.DocumentationId));
        first.Nodes.Where(node => node.Kind == "member" && node.Name == "Handle" &&
                node.DisplayName.StartsWith("Demo.Core.PartialEntity.", StringComparison.Ordinal))
            .Select(node => node.Id).Should().OnlyHaveUniqueItems();
        first.Nodes.Should().ContainSingle(node =>
            node.Kind == "type" && node.Name == "PartialEntity" &&
            node.FilePaths.Count == 2 && node.Modifiers.Contains("partial"));
        first.Nodes.Should().Contain(node =>
            node.Kind == "type" && node.Name == "Envelope" &&
            node.TypeKind == "RecordClass" && node.Arity == 1);
        first.Edges.Should().Contain(edge => edge.Kind == "inherits");
        first.Edges.Should().Contain(edge => edge.Kind == "implements");
        first.Edges.Should().Contain(edge => edge.Kind == "project-reference");
        first.Edges.Should().Contain(edge => edge.Kind == "references");
        var handler = first.Nodes.Single(node =>
            node.Kind == "member" && node.Name == "Handle" &&
            node.DisplayName.Contains("EnvelopeHandler", StringComparison.Ordinal));
        var constructed = first.Nodes.Single(node =>
            node.Kind == "type" && node.Name == "PartialEntity");
        first.Edges.Should().Contain(edge =>
            edge.Kind == "constructs" &&
            edge.FromNodeId == handler.Id &&
            edge.ToNodeId == constructed.Id);
        first.Nodes.Should().Contain(node =>
            node.Kind == "member" && node.Name == "Find" &&
            node.DisplayName.Contains('?'));
        first.Nodes.Should().Contain(node =>
            node.Kind == "member" && node.Name == "Map" &&
            node.Modifiers.Contains("constraint:T:notnull"));
        (await fixture.StatusAsync()).Should().BeEmpty(
            "Roslyn design-time builds must not create intermediate files in the baseline");
    }

    [Fact]
    public async Task Build_EnforcesCancellationAndEstimatedMemoryLimit()
    {
        await using var fixture = await SymbolGraphFixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var cancelledAction = () => new RoslynSymbolGraphBuilder().BuildAsync(
            fixture.Path,
            snapshot,
            cancellationToken: cancelled.Token);
        await cancelledAction.Should().ThrowAsync<OperationCanceledException>();

        var limitedAction = () => new RoslynSymbolGraphBuilder().BuildAsync(
            fixture.Path,
            snapshot,
            new CSharpSymbolGraphLimits { MaxEstimatedMemoryBytes = 1 },
            CancellationToken.None);
        await limitedAction.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*estimated memory limit*");
    }

    [Fact]
    public async Task Build_RestoresTheInheritedMsBuildEnvironment()
    {
        await using var fixture = await SymbolGraphFixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync();
        var variables = new[]
        {
            "MSBUILD_EXE_PATH",
            "MSBuildExtensionsPath",
            "MSBuildSDKsPath"
        };
        var original = variables.ToDictionary(
            variable => variable,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var variable in variables)
                Environment.SetEnvironmentVariable(variable, $"aecs-inherited-{variable}");

            await new RoslynSymbolGraphBuilder().BuildAsync(
                fixture.Path,
                snapshot,
                cancellationToken: default);

            variables.Should().OnlyContain(variable =>
                Environment.GetEnvironmentVariable(variable) == $"aecs-inherited-{variable}");
        }
        finally
        {
            foreach (var variable in variables)
                Environment.SetEnvironmentVariable(variable, original[variable]);
        }
    }

    [Fact]
    public async Task ContextCompiler_UsesOnlyTheAuthenticatedRoslynGraphAsSemanticAuthority()
    {
        await using var fixture = await SymbolGraphFixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync();
        var graph = await new RoslynSymbolGraphBuilder().BuildAsync(
            fixture.Path,
            snapshot,
            cancellationToken: default);
        var contract = new TaskContract
        {
            Id = "TASK-GRAPH-CONTEXT",
            Objective = "Change PartialEntity Handle overload",
            AcceptanceCriteria = ["Existing overloads remain compatible"],
            Scope = new ScopeDefinition { Allowed = ["src/**"] },
            Budget = ExecutionBudget.Default
        };

        var context = new RepositoryContextCompiler().Compile(
            fixture.Path,
            contract,
            snapshot.BaselineCommit,
            symbolGraph: graph);

        context.Manifest.SemanticIndex.Should().Be("roslyn-symbol-graph");
        context.Manifest.SymbolGraphHash.Should().Be(graph.GraphHash);
        context.Manifest.RepositorySnapshotHash.Should().Be(snapshot.SnapshotHash);
        context.Manifest.SchemaVersion.Should().Be(ContextManifestSchema.CurrentVersion);
        context.Manifest.Selections.Should().HaveCount(context.Manifest.EligibleFileCount)
            .And.Contain(selection => selection.Depth > 0);
        context.Manifest.ManifestHash.Should().Be(
            ContextManifestFingerprint.Create(context.Manifest));
        context.Prompt.Should().Contain("Symbols:");
        context.Prompt.Should().Contain("PartialEntity");
    }

    [Fact]
    public async Task AgronomoPlus_LoadsTheRealSubdirectorySolutionAndReferencedProjects()
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

        var graph = await new RoslynSymbolGraphBuilder().BuildAsync(
            workspace.Path,
            snapshot,
            cancellationToken: default);

        if (!await HasSdk9Async(repository.ProcessRunner))
        {
            graph.LoadSucceeded.Should().BeFalse();
            graph.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == "AECSROSSDK" ||
                diagnostic.Message.Contains("compatible .NET SDK", StringComparison.OrdinalIgnoreCase));
            return;
        }

        graph.Projects.Should().HaveCount(3, string.Join("; ", graph.Diagnostics.Select(
            diagnostic => diagnostic.Message)));
        graph.Projects.Should().Contain(project =>
            project.Path == "Backend/AgronomoPlus.Application/AgronomoPlus.Application.csproj" &&
            project.ProjectReferences.Contains(
                "Backend/AgronomoPlus.Domain/AgronomoPlus.Domain.csproj"));
        graph.Nodes.Should().Contain(node =>
            node.Kind == "type" && node.ProjectPath.StartsWith("Backend/", StringComparison.Ordinal));
        graph.Edges.Should().Contain(edge => edge.Kind == "project-reference");
    }

    [Fact]
    public async Task AecsRepository_LoadsItsOwnSolutionsAndSemanticTypes()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var runner = new SystemProcessRunner();
        var head = (await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = ["rev-parse", "HEAD"],
            WorkingDirectory = root,
            Timeout = TimeSpan.FromSeconds(30)
        }, CancellationToken.None)).StandardOutput.Trim();
        var snapshot = await new RepositorySnapshotBuilder(runner).BuildAsync(
            root,
            head,
            new TaskContract
            {
                Id = "TASK-AECS-SELF-GRAPH",
                Objective = "Analyze AECS",
                Execution = new RepositoryExecutionProfile { Target = "AECS.slnx" }
            },
            ToolCommands(),
            CancellationToken.None);

        var graph = await new RoslynSymbolGraphBuilder().BuildAsync(
            root,
            snapshot,
            cancellationToken: default);

        if (!await HasSdk9Async(runner))
        {
            graph.LoadSucceeded.Should().BeFalse();
            graph.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == "AECSROSSDK" ||
                diagnostic.Message.Contains("compatible .NET SDK", StringComparison.OrdinalIgnoreCase));
            return;
        }

        graph.Projects.Should().HaveCount(
            snapshot.Projects.Count(project => project.Language == "C#"),
            string.Join("; ", graph.Diagnostics.Select(diagnostic => diagnostic.Message)));
        graph.Nodes.Should().Contain(node =>
            node.Kind == "type" && node.DisplayName.EndsWith(
                ".RepositorySnapshot",
                StringComparison.Ordinal));
        graph.Nodes.Count.Should().BeGreaterThan(100);
        graph.Edges.Count.Should().BeGreaterThan(100);
    }

    private static List<ExecutionCommandEvidence> ToolCommands() =>
    [
        new()
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            ExitCode = 0,
            StandardOutput = "test-sdk"
        }
    ];

    private static async Task<bool> HasSdk9Async(IProcessRunner runner)
    {
        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "dotnet",
            Arguments = ["--list-sdks"],
            WorkingDirectory = Path.GetTempPath(),
            Timeout = TimeSpan.FromSeconds(30)
        }, CancellationToken.None);
        return result.Succeeded && result.StandardOutput.Split('\n').Any(line =>
            line.TrimStart().StartsWith("9.", StringComparison.Ordinal));
    }

    private sealed class SymbolGraphFixture : IAsyncDisposable
    {
        private const string RootPrefix = "aecs-roslyn-symbol-graph-tests-";
        private readonly SystemProcessRunner _runner = new();

        private SymbolGraphFixture(string path) => Path = path;

        public string Path { get; }

        public static async Task<SymbolGraphFixture> CreateAsync()
        {
            var fixture = new SymbolGraphFixture(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{RootPrefix}{Guid.NewGuid():N}"));
            Directory.CreateDirectory(fixture.Path);
            await fixture.WriteAsync("src/Core/Core.csproj", Project());
            await fixture.WriteAsync("src/Core/Contracts.cs", """
                namespace Demo.Core;
                public interface IHandler<T> { T Handle(T value); }
                public abstract class Entity { public string Id { get; init; } = ""; }
                public sealed record Envelope<T>(T Value);
                public sealed class Api
                {
                    public string? Find(string? value) => value;
                    public T Map<T>(T value) where T : notnull => value;
                }
                """);
            await fixture.WriteAsync("src/Core/PartialEntity.Part1.cs", """
                namespace Demo.Core;
                public partial class PartialEntity : Entity
                {
                    public int Handle(int value) => value;
                }
                """);
            await fixture.WriteAsync("src/Core/PartialEntity.Part2.cs", """
                namespace Demo.Core;
                public partial class PartialEntity
                {
                    public string Handle(string value) => value;
                }
                """);
            await fixture.WriteAsync("src/Core/MissingReference.cs", """
                namespace Demo.Core;
                public sealed class MissingReference
                {
                    public Missing.Type Value => null!;
                }
                """);
            await fixture.WriteAsync(
                "src/App/App.csproj",
                Project("../Core/Core.csproj"));
            await fixture.WriteAsync("src/App/EnvelopeHandler.cs", """
                using Demo.Core;
                namespace Demo.App;
                public sealed class EnvelopeHandler : IHandler<Envelope<string>>
                {
                    public Envelope<string> Handle(Envelope<string> value)
                    {
                        var entity = new PartialEntity();
                        entity.Handle(value.Value);
                        return value;
                    }
                }
                """);
            await fixture.WriteAsync("Repo.slnx", """
                <Solution>
                  <Project Path="src/Core/Core.csproj" />
                  <Project Path="src/App/App.csproj" />
                </Solution>
                """);
            await fixture.GitAsync("init", "--initial-branch=fixture");
            await fixture.GitAsync("config", "user.email", "aecs-roslyn@example.invalid");
            await fixture.GitAsync("config", "user.name", "AECS Roslyn Tests");
            await fixture.GitAsync("config", "core.autocrlf", "false");
            await fixture.GitAsync("add", "-A", "--");
            await fixture.GitAsync("commit", "-m", "semantic graph fixture");
            return fixture;
        }

        public async Task<RepositorySnapshot> SnapshotAsync()
        {
            var head = (await GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
            return await new RepositorySnapshotBuilder(_runner).BuildAsync(
                Path,
                head,
                new TaskContract
                {
                    Id = "TASK-ROSLYN-GRAPH",
                    Objective = "Build the semantic graph",
                    Execution = new RepositoryExecutionProfile { Target = "Repo.slnx" }
                },
                ToolCommands(),
                CancellationToken.None);
        }

        public async Task<string> StatusAsync() =>
            (await GitAsync("status", "--porcelain=v1", "--untracked-files=all"))
            .StandardOutput;

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

        private async Task<ProcessExecutionResult> GitAsync(params string[] arguments)
        {
            var result = await _runner.RunAsync(new ProcessExecutionRequest
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

        private static string Project(string? reference = null) =>
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable>" +
            "<ImplicitUsings>enable</ImplicitUsings><Deterministic>true</Deterministic>" +
            "</PropertyGroup>" +
            (reference is null
                ? string.Empty
                : $"<ItemGroup><ProjectReference Include=\"{reference}\" /></ItemGroup>") +
            "</Project>";
    }
}
