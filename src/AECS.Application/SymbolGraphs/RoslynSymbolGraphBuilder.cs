using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AECS.Domain.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace AECS.Application.SymbolGraphs;

public sealed partial class RoslynSymbolGraphBuilder : ICSharpSymbolGraphBuilder
{
    private static readonly object RegistrationLock = new();
    private static readonly SemaphoreSlim WorkspaceBuildLock = new(1, 1);
    private static string? _registeredMsBuildVersion;
    private static string? _registeredSdkVersion;
    private static string? _registeredMsBuildPath;
    private static readonly string[] MsBuildEnvironmentVariables =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath"
    ];
    private static readonly IDictionary<string, string> GlobalProperties =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["BuildProjectReferences"] = "false",
            ["Configuration"] = "Release",
            ["DefaultItemExcludes"] = "**/bin/**;**/obj/**",
            ["DesignTimeBuild"] = "true",
            ["Platform"] = "AnyCPU",
            ["ProvideCommandLineArgs"] = "true",
            ["RestoreIgnoreFailedSources"] = "true",
            ["SkipCompilerExecution"] = "true"
        };

    public async Task<CSharpSymbolGraph> BuildAsync(
        string workspacePath,
        RepositorySnapshot repositorySnapshot,
        CSharpSymbolGraphLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositorySnapshot);
        limits ??= new CSharpSymbolGraphLimits();
        ValidateLimits(limits);
        var root = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"C# symbol graph root not found: {root}");
        if (string.IsNullOrWhiteSpace(repositorySnapshot.SnapshotHash))
            throw new InvalidOperationException("C# symbol graph requires a repository snapshot hash.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.MaxDurationSeconds));
        var token = timeout.Token;
        token.ThrowIfCancellationRequested();
        var lockTaken = false;
        IReadOnlyDictionary<string, string?>? inheritedEnvironment = null;
        try
        {
            await WorkspaceBuildLock.WaitAsync(token);
            lockTaken = true;
            inheritedEnvironment = CaptureMsBuildEnvironment();
            return await BuildCoreAsync(root, repositorySnapshot, limits, token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"C# symbol graph exceeded its {limits.MaxDurationSeconds}-second limit.");
        }
        finally
        {
            if (inheritedEnvironment is not null)
                RestoreMsBuildEnvironment(inheritedEnvironment);
            if (lockTaken)
                WorkspaceBuildLock.Release();
        }
    }

    private static async Task<CSharpSymbolGraph> BuildCoreAsync(
        string root,
        RepositorySnapshot snapshot,
        CSharpSymbolGraphLimits limits,
        CancellationToken cancellationToken)
    {
        var compilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        using var isolatedWorkspace = CreateIsolatedWorkspace(
            root,
            snapshot,
            cancellationToken);
        var analysisRoot = isolatedWorkspace.Path;
        var collector = new GraphCollector(analysisRoot, snapshot, limits, cancellationToken);
        string msBuildVersion;
        var sdkVersion = "unavailable";
        try
        {
            var registration = RegisterMsBuild(root);
            msBuildVersion = registration.MsBuildVersion;
            sdkVersion = registration.SdkVersion;
            if (!string.IsNullOrWhiteSpace(registration.Diagnostic))
            {
                collector.AddWorkspaceDiagnostic(
                    "Failure",
                    registration.Diagnostic,
                    string.Empty,
                    isFailure: true,
                    code: "AECSROSSDK");
            }
        }
        catch (Exception ex)
        {
            collector.AddWorkspaceDiagnostic(
                "Failure",
                ex.Message,
                string.Empty,
                isFailure: true,
                code: "AECSROSSDK");
            return collector.CreateGraph(
                compilerVersion,
                "unavailable",
                sdkVersion,
                GlobalProperties);
        }
        foreach (var project in snapshot.Projects
                     .OrderBy(project => project.Path, StringComparer.Ordinal))
        {
            collector.AddProjectNode(project);
        }

        var roots = DiscoverRoots(snapshot);
        var processedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loadRoot in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            collector.CheckLimits();
            try
            {
                using var workspace = MSBuildWorkspace.Create(GlobalProperties);
                workspace.SkipUnrecognizedProjects = false;
                workspace.LoadMetadataForReferencedProjects = false;
                workspace.RegisterWorkspaceFailedHandler(eventArgs =>
                    collector.AddWorkspaceDiagnostic(
                        eventArgs.Diagnostic.Kind.ToString(),
                        eventArgs.Diagnostic.Message,
                        loadRoot.Path,
                        eventArgs.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure));

                var fullPath = ResolveSnapshotPath(analysisRoot, loadRoot.Path, snapshot);
                Solution solution;
                if (loadRoot.Kind == "solution")
                {
                    solution = await workspace.OpenSolutionAsync(
                        fullPath,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    var openedProject = await workspace.OpenProjectAsync(
                        fullPath,
                        cancellationToken: cancellationToken);
                    solution = openedProject.Solution;
                }

                var projectMaps = CreateProjectMaps(analysisRoot, solution, snapshot);
                foreach (var project in solution.Projects
                             .Where(project => project.Language == LanguageNames.CSharp)
                             .OrderBy(
                                 project => RelativeProjectPath(analysisRoot, project),
                                 StringComparer.Ordinal))
                {
                    var projectPath = SnapshotProjectPath(
                        snapshot,
                        RelativeProjectPath(analysisRoot, project));
                    if (string.IsNullOrWhiteSpace(projectPath) ||
                        !processedProjects.Add(projectPath))
                    {
                        continue;
                    }

                    await ProcessProjectAsync(
                        analysisRoot,
                        project,
                        projectPath,
                        snapshot,
                        projectMaps,
                        collector,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                collector.AddWorkspaceDiagnostic(
                    "Failure",
                    ex.Message,
                    loadRoot.Path,
                    isFailure: true,
                    code: "AECSROS001");
            }
        }

        if (processedProjects.Count == 0 && snapshot.Projects.Any(project => project.Language == "C#"))
        {
            collector.AddWorkspaceDiagnostic(
                "Failure",
                "No C# project could be loaded from the repository snapshot.",
                string.Empty,
                isFailure: true,
                code: "AECSROS002");
        }

        return collector.CreateGraph(
            compilerVersion,
            msBuildVersion,
            sdkVersion,
            GlobalProperties);
    }

    private static async Task ProcessProjectAsync(
        string root,
        Project project,
        string projectPath,
        RepositorySnapshot snapshot,
        ProjectMaps maps,
        GraphCollector collector,
        CancellationToken cancellationToken)
    {
        collector.CheckLimits();
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is not CSharpCompilation csharpCompilation)
        {
            collector.AddWorkspaceDiagnostic(
                "Failure",
                "Roslyn did not produce a C# compilation for the loaded project.",
                projectPath,
                isFailure: true,
                code: "AECSROS003");
            return;
        }

        collector.AddProject(CreateProject(root, project, projectPath, snapshot, csharpCompilation));
        AddProjectReferenceEdges(root, project, projectPath, snapshot, collector);
        var includedDocuments = project.Documents
            .Select(document => new
            {
                Document = document,
                Path = SnapshotFilePath(snapshot, RelativeDocumentPath(root, document))
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToList();
        collector.AddDocuments(includedDocuments.Count);

        foreach (var item in includedDocuments)
            collector.AddFileNode(projectPath, item.Path);

        AddNamespace(
            csharpCompilation.Assembly.GlobalNamespace,
            projectPath,
            maps,
            collector,
            cancellationToken);

        foreach (var diagnostic in csharpCompilation.GetDiagnostics(cancellationToken)
                     .Where(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                     .OrderBy(item => item.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(item => item.Location.SourceSpan.Start)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            collector.AddCompilerDiagnostic(projectPath, diagnostic);
        }

        foreach (var item in includedDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            collector.CheckLimits();
            var rootNode = await item.Document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await item.Document.GetSemanticModelAsync(cancellationToken);
            if (rootNode is null || semanticModel is null)
            {
                collector.AddWorkspaceDiagnostic(
                    "Failure",
                    $"Roslyn could not create a semantic model for '{item.Path}'.",
                    projectPath,
                    isFailure: true,
                    code: "AECSROS004");
                continue;
            }

            foreach (var name in rootNode.DescendantNodes(descendIntoTrivia: false)
                         .OfType<SimpleNameSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveReferencedSymbol(semanticModel.GetSymbolInfo(name, cancellationToken));
                var owner = NormalizeGraphSymbol(
                    semanticModel.GetEnclosingSymbol(name.SpanStart, cancellationToken));
                if (target is null || owner is null ||
                    (target.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.TypeParameter) ||
                    (owner.Kind is SymbolKind.Local or SymbolKind.Parameter))
                {
                    continue;
                }

                var ownerNode = collector.AddSymbolNode(owner, projectPath, maps);
                var targetNode = collector.AddSymbolNode(target, projectPath, maps);
                if (ownerNode is not null && targetNode is not null && ownerNode.Id != targetNode.Id)
                    collector.AddEdge("references", ownerNode.Id, targetNode.Id);
            }

            foreach (var creation in rootNode.DescendantNodes(descendIntoTrivia: false)
                         .OfType<BaseObjectCreationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = semanticModel.GetTypeInfo(creation, cancellationToken).Type;
                var owner = NormalizeGraphSymbol(
                    semanticModel.GetEnclosingSymbol(creation.SpanStart, cancellationToken));
                if (target is null || owner is null ||
                    owner.Kind is SymbolKind.Local or SymbolKind.Parameter)
                {
                    continue;
                }

                var ownerNode = collector.AddSymbolNode(owner, projectPath, maps);
                var targetNode = collector.AddSymbolNode(target, projectPath, maps);
                if (ownerNode is not null && targetNode is not null && ownerNode.Id != targetNode.Id)
                    collector.AddEdge("constructs", ownerNode.Id, targetNode.Id);
            }
        }
    }

    private static void AddNamespace(
        INamespaceSymbol namespaceSymbol,
        string projectPath,
        ProjectMaps maps,
        GraphCollector collector,
        CancellationToken cancellationToken)
    {
        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers()
                     .OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddNamespace(childNamespace, projectPath, maps, collector, cancellationToken);
        }

        foreach (var type in namespaceSymbol.GetTypeMembers()
                     .OrderBy(item => item.MetadataName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddType(type, projectPath, maps, collector, cancellationToken);
        }
    }

    private static void AddType(
        INamedTypeSymbol type,
        string projectPath,
        ProjectMaps maps,
        GraphCollector collector,
        CancellationToken cancellationToken)
    {
        if (!collector.IsIncludedDeclaration(type))
            return;
        var typeNode = collector.AddSymbolNode(type, projectPath, maps);
        if (typeNode is null || typeNode.IsExternal)
            return;

        if (type.BaseType is not null)
        {
            var baseNode = collector.AddSymbolNode(type.BaseType, projectPath, maps);
            if (baseNode is not null)
                collector.AddEdge("inherits", typeNode.Id, baseNode.Id);
        }
        foreach (var interfaceType in type.Interfaces.OrderBy(
                     item => item.ToDisplayString(),
                     StringComparer.Ordinal))
        {
            var interfaceNode = collector.AddSymbolNode(
                interfaceType,
                projectPath,
                maps);
            if (interfaceNode is not null)
                collector.AddEdge("implements", typeNode.Id, interfaceNode.Id);
        }

        foreach (var member in type.GetMembers()
                     .Where(member => IsSupportedMember(member) &&
                         collector.IsIncludedDeclaration(member))
                     .OrderBy(item => DocumentationCommentId.CreateDeclarationId(item) ??
                         item.ToDisplayString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            collector.AddSymbolNode(member, projectPath, maps);
        }
        foreach (var nested in type.GetTypeMembers().OrderBy(
                     item => item.MetadataName,
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddType(nested, projectPath, maps, collector, cancellationToken);
        }
    }

    private static bool IsSupportedMember(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared || !symbol.Locations.Any(location => location.IsInSource))
            return false;
        if (symbol is IMethodSymbol method && method.MethodKind is
            MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or
            MethodKind.EventRemove or MethodKind.EventRaise or MethodKind.AnonymousFunction or
            MethodKind.LocalFunction)
        {
            return false;
        }
        return symbol.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or
            SymbolKind.Event;
    }

    private static ISymbol? ResolveReferencedSymbol(SymbolInfo info)
    {
        var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1
            ? info.CandidateSymbols[0]
            : null);
        if (symbol is IAliasSymbol alias)
            symbol = alias.Target;
        return NormalizeGraphSymbol(symbol);
    }

    private static ISymbol? NormalizeGraphSymbol(ISymbol? symbol)
    {
        while (symbol is not null && symbol.Kind is
               SymbolKind.Local or SymbolKind.Parameter or SymbolKind.TypeParameter or SymbolKind.RangeVariable)
        {
            symbol = symbol.ContainingSymbol;
        }
        if (symbol is IMethodSymbol method)
            return (method.ReducedFrom ?? method).OriginalDefinition;
        return symbol?.OriginalDefinition;
    }

    private static CSharpSymbolGraphProject CreateProject(
        string root,
        Project project,
        string projectPath,
        RepositorySnapshot snapshot,
        CSharpCompilation compilation)
    {
        var parse = project.ParseOptions as CSharpParseOptions;
        var options = compilation.Options;
        var snapshotProject = snapshot.Projects.First(item =>
            item.Path.Equals(projectPath, StringComparison.OrdinalIgnoreCase));
        var projectReferences = project.ProjectReferences
            .Select(reference => project.Solution.GetProject(reference.ProjectId))
            .Where(reference => reference is not null)
            .Select(reference => SnapshotProjectPath(
                snapshot,
                RelativeProjectPath(root, reference!)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var result = new CSharpSymbolGraphProject
        {
            Id = CSharpSymbolGraphFingerprint.StableProjectId(projectPath),
            Path = projectPath,
            Name = project.Name,
            AssemblyName = project.AssemblyName ?? compilation.AssemblyName ?? string.Empty,
            TargetFrameworks = snapshotProject.Frameworks.OrderBy(
                framework => framework,
                StringComparer.Ordinal).ToList(),
            LanguageVersion = parse?.LanguageVersion.ToDisplayString() ?? string.Empty,
            DocumentationMode = parse?.DocumentationMode.ToString() ?? string.Empty,
            PreprocessorSymbols = parse?.PreprocessorSymbolNames.OrderBy(
                symbol => symbol,
                StringComparer.Ordinal).ToList() ?? [],
            OutputKind = options.OutputKind.ToString(),
            NullableContext = options.NullableContextOptions.ToString(),
            OptimizationLevel = options.OptimizationLevel.ToString(),
            Platform = options.Platform.ToString(),
            AllowUnsafe = options.AllowUnsafe,
            Deterministic = options.Deterministic,
            ProjectReferences = projectReferences
        };
        return WithProjectHash(result);
    }

    private static void AddProjectReferenceEdges(
        string root,
        Project project,
        string projectPath,
        RepositorySnapshot snapshot,
        GraphCollector collector)
    {
        var from = CSharpSymbolGraphFingerprint.StableNodeId($"project|{projectPath}");
        foreach (var reference in project.ProjectReferences
                     .Select(item => project.Solution.GetProject(item.ProjectId))
                     .Where(item => item is not null)
                     .OrderBy(item => item!.FilePath, StringComparer.Ordinal))
        {
            var targetPath = SnapshotProjectPath(
                snapshot,
                RelativeProjectPath(root, reference!));
            if (string.IsNullOrWhiteSpace(targetPath))
                continue;
            var to = CSharpSymbolGraphFingerprint.StableNodeId($"project|{targetPath}");
            collector.AddEdge("project-reference", from, to);
        }
    }

    private static ProjectMaps CreateProjectMaps(
        string root,
        Solution solution,
        RepositorySnapshot snapshot)
    {
        var assemblies = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects
                     .Where(project => project.Language == LanguageNames.CSharp)
                     .OrderBy(project => project.FilePath, StringComparer.Ordinal))
        {
            var projectPath = SnapshotProjectPath(
                snapshot,
                RelativeProjectPath(root, project));
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(project.AssemblyName))
                assemblies.TryAdd(project.AssemblyName, projectPath);
            foreach (var document in project.Documents)
            {
                var documentPath = SnapshotFilePath(
                    snapshot,
                    RelativeDocumentPath(root, document));
                if (!string.IsNullOrWhiteSpace(documentPath))
                    files.TryAdd(documentPath, projectPath);
            }
        }
        return new ProjectMaps(assemblies, files);
    }

    private static List<LoadRoot> DiscoverRoots(RepositorySnapshot snapshot)
    {
        var csharpProjects = snapshot.Projects.Where(project => project.Language == "C#")
            .Select(project => project.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<LoadRoot>();
        foreach (var solution in snapshot.Solutions.OrderBy(
                     solution => solution.Path,
                     StringComparer.Ordinal))
        {
            var containsCSharp = solution.Projects.Any(csharpProjects.Contains);
            if (!containsCSharp)
                continue;
            roots.Add(new LoadRoot("solution", solution.Path));
            coveredProjects.UnionWith(solution.Projects.Where(csharpProjects.Contains));
        }
        roots.AddRange(csharpProjects.Except(coveredProjects, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new LoadRoot("project", path)));
        return roots;
    }

    private static IsolatedWorkspace CreateIsolatedWorkspace(
        string sourceRoot,
        RepositorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var isolatedRoot = Path.Combine(
            Path.GetTempPath(),
            "aecs-roslyn-workspaces",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(isolatedRoot);
        try
        {
            foreach (var file in snapshot.Files.OrderBy(
                         file => file.Path,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolveContainedFile(sourceRoot, file.Path);
                var targetPath = ResolveContainedPath(isolatedRoot, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath, overwrite: false);
            }

            CopyRestoreArtifacts(sourceRoot, isolatedRoot, snapshot, cancellationToken);
            return new IsolatedWorkspace(isolatedRoot);
        }
        catch
        {
            DeleteIsolatedWorkspace(isolatedRoot);
            throw;
        }
    }

    private static void CopyRestoreArtifacts(
        string sourceRoot,
        string isolatedRoot,
        RepositorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var project in snapshot.Projects
                     .Where(project => project.Language == "C#")
                     .OrderBy(project => project.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = ResolveContainedFile(sourceRoot, project.Path);
            var sourceDirectory = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj");
            if (!Directory.Exists(sourceDirectory) ||
                (File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var relativeProjectDirectory = Path.GetDirectoryName(project.Path.Replace(
                '/',
                Path.DirectorySeparatorChar)) ?? string.Empty;
            var targetDirectory = ResolveContainedPath(
                isolatedRoot,
                Path.Combine(relativeProjectDirectory, "obj"));
            foreach (var sourcePath in Directory.EnumerateFiles(
                         sourceDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly)
                     .Where(path => IsRestoreArtifact(path) &&
                         (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                     .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));
                Directory.CreateDirectory(targetDirectory);
                File.Copy(sourcePath, targetPath, overwrite: false);
            }
        }
    }

    private static bool IsRestoreArtifact(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("project.nuget.cache", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".nuget.dgspec.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveContainedFile(string root, string relativePath)
    {
        var fullPath = ResolveContainedPath(root, relativePath);
        if (!File.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Symbol graph input is unsafe or unavailable: '{relativePath}'.");
        }
        return fullPath;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException(
                $"Symbol graph path escapes its workspace: '{relativePath}'.");
        }
        return fullPath;
    }

    private static void DeleteIsolatedWorkspace(string isolatedRoot)
    {
        try
        {
            var temporaryParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "aecs-roslyn-workspaces"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(isolatedRoot);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(temporaryParent, comparison))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete unexpected Roslyn workspace path: {fullPath}");
            }
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup must not invalidate an otherwise reproducible graph.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup must not invalidate an otherwise reproducible graph.
        }
    }

    private static MsBuildRegistration RegisterMsBuild(string workingDirectory)
    {
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                string? diagnostic = null;
                VisualStudioInstance? instance;
                try
                {
                    instance = QueryMsBuildInstances(workingDirectory).FirstOrDefault();
                }
                catch (Exception ex)
                {
                    diagnostic = ex.Message;
                    instance = QueryMsBuildInstances(Path.GetTempPath()).FirstOrDefault();
                }
                if (instance is null)
                {
                    // Fallback: try RegisterDefaults which uses environment detection
                    try
                    {
                        var defaultsResult = MSBuildLocator.RegisterDefaults();
                        _registeredMsBuildPath = defaultsResult.MSBuildPath;
                        _registeredMsBuildVersion = defaultsResult.Version.ToString();
                        _registeredSdkVersion = ReadSdkVersion(defaultsResult);
                        return new MsBuildRegistration(
                            _registeredMsBuildVersion,
                            _registeredSdkVersion,
                            diagnostic ?? "Used RegisterDefaults fallback");
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "No compatible .NET SDK/MSBuild instance could be detected for the baseline. " +
                            $"Query failed: {diagnostic ?? "no instance found"}. " +
                            $"RegisterDefaults failed: {ex.Message}");
                    }
                }
                MSBuildLocator.RegisterInstance(instance);
                _registeredMsBuildPath = instance.MSBuildPath;
                _registeredMsBuildVersion = ReadMsBuildVersion(instance);
                _registeredSdkVersion = ReadSdkVersion(instance);
                return new MsBuildRegistration(
                    _registeredMsBuildVersion,
                    _registeredSdkVersion,
                    diagnostic);
            }
            ApplyRegisteredMsBuildEnvironment();
            string? registeredDiagnostic = null;
            try
            {
                var requested = QueryMsBuildInstances(workingDirectory).FirstOrDefault();
                if (requested is null)
                {
                    registeredDiagnostic =
                        "No compatible .NET SDK/MSBuild instance could be detected for the baseline.";
                }
                else
                {
                    var requestedSdkVersion = ReadSdkVersion(requested);
                    if (!string.Equals(
                            requestedSdkVersion,
                            _registeredSdkVersion,
                            StringComparison.Ordinal))
                    {
                        registeredDiagnostic =
                            $"The baseline resolves .NET SDK '{requestedSdkVersion}', but this process " +
                            $"already uses '{_registeredSdkVersion}'.";
                    }
                }
            }
            catch (Exception ex)
            {
                registeredDiagnostic = ex.Message;
            }
            return new MsBuildRegistration(
                _registeredMsBuildVersion ?? "registered",
                _registeredSdkVersion ?? "registered",
                registeredDiagnostic);
        }
    }

    private static IReadOnlyDictionary<string, string?> CaptureMsBuildEnvironment() =>
        MsBuildEnvironmentVariables.ToDictionary(
            variable => variable,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);

    private static void RestoreMsBuildEnvironment(
        IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var variable in MsBuildEnvironmentVariables)
            Environment.SetEnvironmentVariable(variable, environment[variable]);
    }

    private static void ApplyRegisteredMsBuildEnvironment()
    {
        if (string.IsNullOrWhiteSpace(_registeredMsBuildPath))
            return;
        Environment.SetEnvironmentVariable(
            "MSBUILD_EXE_PATH",
            Path.Combine(_registeredMsBuildPath, "MSBuild.dll"));
        Environment.SetEnvironmentVariable("MSBuildExtensionsPath", _registeredMsBuildPath);
        Environment.SetEnvironmentVariable(
            "MSBuildSDKsPath",
            Path.Combine(_registeredMsBuildPath, "Sdks"));
    }

    private static string ReadMsBuildVersion(VisualStudioInstance instance)
    {
        var assemblyPath = Path.Combine(instance.MSBuildPath, "MSBuild.dll");
        if (!File.Exists(assemblyPath))
            return instance.Version.ToString();
        var version = FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion;
        return string.IsNullOrWhiteSpace(version) ? instance.Version.ToString() : version;
    }

    private static string ReadSdkVersion(VisualStudioInstance instance)
    {
        var path = instance.MSBuildPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var version = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(version) ? instance.Version.ToString() : version;
    }

    private static IEnumerable<VisualStudioInstance> QueryMsBuildInstances(
        string workingDirectory) => MSBuildLocator.QueryVisualStudioInstances(
            new VisualStudioInstanceQueryOptions
            {
                AllowAllRuntimeVersions = true,
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = workingDirectory
            }).OrderByDescending(item => item.Version);

    private static string ResolveSnapshotPath(
        string root,
        string relativePath,
        RepositorySnapshot snapshot)
    {
        if (!snapshot.Files.Any(file =>
                file.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Symbol graph root is absent from the repository snapshot: '{relativePath}'.");
        }
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison) || !File.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Symbol graph root is unsafe or unavailable: '{relativePath}'.");
        }
        return fullPath;
    }

    private static string RelativeProjectPath(string root, Project project) =>
        string.IsNullOrWhiteSpace(project.FilePath)
            ? string.Empty
            : RelativePath(root, project.FilePath);

    private static string RelativeDocumentPath(string root, Document document) =>
        string.IsNullOrWhiteSpace(document.FilePath)
            ? string.Empty
            : RelativePath(root, document.FilePath);

    private static string SnapshotProjectPath(
        RepositorySnapshot snapshot,
        string candidate) => snapshot.Projects.FirstOrDefault(project =>
            project.Path.Equals(candidate, StringComparison.OrdinalIgnoreCase))?.Path ?? string.Empty;

    private static string SnapshotFilePath(
        RepositorySnapshot snapshot,
        string candidate) => snapshot.Files.FirstOrDefault(file =>
            file.Language == "C#" &&
            file.Path.Equals(candidate, StringComparison.OrdinalIgnoreCase))?.Path ?? string.Empty;

    private static string RelativePath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(fullPath)).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." ||
            Path.IsPathRooted(relative)
            ? string.Empty
            : relative;
    }

    private static void ValidateLimits(CSharpSymbolGraphLimits limits)
    {
        if (limits.MaxDurationSeconds <= 0 || limits.MaxEstimatedMemoryBytes <= 0 ||
            limits.MaxProjects <= 0 || limits.MaxDocuments <= 0 || limits.MaxNodes <= 0 ||
            limits.MaxEdges <= 0 || limits.MaxDiagnostics <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "C# symbol graph limits must be positive.");
        }
    }

    private static CSharpSymbolGraphProject WithProjectHash(CSharpSymbolGraphProject project) => new()
    {
        Id = project.Id,
        Hash = CSharpSymbolGraphFingerprint.CreateProject(project),
        Path = project.Path,
        Name = project.Name,
        AssemblyName = project.AssemblyName,
        TargetFrameworks = project.TargetFrameworks,
        LanguageVersion = project.LanguageVersion,
        DocumentationMode = project.DocumentationMode,
        PreprocessorSymbols = project.PreprocessorSymbols,
        OutputKind = project.OutputKind,
        NullableContext = project.NullableContext,
        OptimizationLevel = project.OptimizationLevel,
        Platform = project.Platform,
        AllowUnsafe = project.AllowUnsafe,
        Deterministic = project.Deterministic,
        ProjectReferences = project.ProjectReferences
    };

    private sealed record LoadRoot(string Kind, string Path);

    private sealed class IsolatedWorkspace(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose() => DeleteIsolatedWorkspace(Path);
    }

    private sealed record MsBuildRegistration(
        string MsBuildVersion,
        string SdkVersion,
        string? Diagnostic);

    private sealed record ProjectMaps(
        IReadOnlyDictionary<string, string> AssemblyProjects,
        IReadOnlyDictionary<string, string> FileProjects);

    private sealed partial class GraphCollector
    {
        private readonly string _root;
        private readonly RepositorySnapshot _snapshot;
        private readonly CSharpSymbolGraphLimits _limits;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, RepositorySnapshotFile> _snapshotFiles;
        private readonly Dictionary<string, CSharpSymbolGraphProject> _projects =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, CSharpSymbolGraphNode> _nodes =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, CSharpSymbolGraphEdge> _edges =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, CSharpSymbolGraphDiagnostic> _diagnostics =
            new(StringComparer.Ordinal);
        private long _estimatedMemory;
        private int _documentCount;
        private bool _loadFailure;

        public GraphCollector(
            string root,
            RepositorySnapshot snapshot,
            CSharpSymbolGraphLimits limits,
            CancellationToken cancellationToken)
        {
            _root = root;
            _snapshot = snapshot;
            _limits = limits;
            _cancellationToken = cancellationToken;
            _snapshotFiles = snapshot.Files.ToDictionary(
                file => file.Path,
                StringComparer.OrdinalIgnoreCase);
        }

        public void AddProjectNode(RepositorySnapshotProject project)
        {
            var node = new CSharpSymbolGraphNode
            {
                Id = CSharpSymbolGraphFingerprint.StableNodeId($"project|{project.Path}"),
                Kind = "project",
                Name = Path.GetFileNameWithoutExtension(project.Path),
                DisplayName = project.Path,
                ProjectPath = project.Path,
                SourceHash = project.Hash
            };
            AddNode(WithNodeHash(node));
        }

        public void AddProject(CSharpSymbolGraphProject project)
        {
            if (_projects.Count >= _limits.MaxProjects && !_projects.ContainsKey(project.Id))
                ThrowLimit("project count", _limits.MaxProjects);
            _projects[project.Id] = project;
            AddMemory(project);
        }

        public void AddDocuments(int count)
        {
            _documentCount = checked(_documentCount + count);
            if (_documentCount > _limits.MaxDocuments)
                ThrowLimit("document count", _limits.MaxDocuments);
        }

        public void AddFileNode(string projectPath, string filePath)
        {
            var snapshotFile = _snapshotFiles[filePath];
            var projectNodeId = CSharpSymbolGraphFingerprint.StableNodeId(
                $"project|{projectPath}");
            var node = new CSharpSymbolGraphNode
            {
                Id = CSharpSymbolGraphFingerprint.StableNodeId(
                    $"file|{projectPath}|{filePath}"),
                Kind = "file",
                Name = Path.GetFileName(filePath),
                DisplayName = filePath,
                ProjectPath = projectPath,
                FilePaths = [filePath],
                SourceHash = snapshotFile.Hash,
                ContainingNodeId = projectNodeId
            };
            node = WithNodeHash(node);
            AddNode(node);
            AddEdge("contains", projectNodeId, node.Id);
        }

        public CSharpSymbolGraphNode? AddSymbolNode(
            ISymbol symbol,
            string currentProjectPath,
            ProjectMaps maps)
        {
            symbol = NormalizeGraphSymbol(symbol) ?? symbol;
            if (symbol is INamespaceSymbol namespaceSymbol && namespaceSymbol.IsGlobalNamespace)
                return null;
            if (symbol.Kind is not (SymbolKind.Namespace or SymbolKind.NamedType or SymbolKind.Method or
                SymbolKind.Property or SymbolKind.Field or SymbolKind.Event))
            {
                return null;
            }

            var files = symbol.Locations.Where(location => location.IsInSource &&
                    !string.IsNullOrWhiteSpace(location.SourceTree?.FilePath))
                .Select(location => RelativePath(_root, location.SourceTree!.FilePath))
                .Select(CanonicalFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            var assemblyName = symbol.ContainingAssembly?.Name ?? string.Empty;
            var projectPath = files.Select(path => maps.FileProjects.GetValueOrDefault(path))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ??
                maps.AssemblyProjects.GetValueOrDefault(assemblyName) ??
                (files.Count > 0 ? currentProjectPath : string.Empty);
            var isExternal = files.Count == 0;
            var documentationId = DocumentationCommentId.CreateDeclarationId(symbol) ?? string.Empty;
            var displayName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var kind = symbol.Kind switch
            {
                SymbolKind.Namespace => "namespace",
                SymbolKind.NamedType => "type",
                _ => "member"
            };
            var identity = $"{kind}|{projectPath}|{assemblyName}|" +
                (string.IsNullOrWhiteSpace(documentationId) ? displayName : documentationId);
            var nodeId = CSharpSymbolGraphFingerprint.StableNodeId(identity);
            if (_nodes.TryGetValue(nodeId, out var existing) &&
                (!existing.IsExternal || files.Count == 0))
            {
                return existing;
            }

            if (symbol.ContainingType is not null)
                AddSymbolNode(symbol.ContainingType, currentProjectPath, maps);
            else if (symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
                AddSymbolNode(containingNamespace, currentProjectPath, maps);

            var containingId = ContainingNodeId(symbol, projectPath, assemblyName);
            var node = new CSharpSymbolGraphNode
            {
                Id = nodeId,
                Kind = kind,
                Name = symbol.Name,
                DisplayName = displayName,
                DocumentationId = documentationId,
                ProjectPath = projectPath,
                FilePaths = files,
                ContainingNodeId = containingId,
                Accessibility = symbol.DeclaredAccessibility.ToString(),
                Modifiers = Modifiers(symbol),
                Arity = symbol is INamedTypeSymbol type ? type.Arity :
                    symbol is IMethodSymbol method ? method.Arity : 0,
                TypeKind = symbol is INamedTypeSymbol namedType
                    ? namedType.IsRecord
                        ? namedType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Struct
                            ? "RecordStruct"
                            : "RecordClass"
                        : namedType.TypeKind.ToString()
                    : string.Empty,
                MemberKind = kind == "member" ? symbol.Kind.ToString() : string.Empty,
                AssemblyName = assemblyName,
                IsExternal = isExternal
            };
            node = WithNodeHash(node);
            AddNode(node);
            if (!string.IsNullOrWhiteSpace(containingId))
                AddEdge("contains", containingId, node.Id);
            foreach (var file in files)
            {
                var ownerProject = string.IsNullOrWhiteSpace(projectPath)
                    ? currentProjectPath
                    : projectPath;
                var fileId = CSharpSymbolGraphFingerprint.StableNodeId(
                    $"file|{ownerProject}|{file}");
                if (_nodes.ContainsKey(fileId))
                    AddEdge("declares", fileId, node.Id);
            }
            return node;
        }

        public bool IsIncludedDeclaration(ISymbol symbol) => symbol.Locations.Any(location =>
            location.IsInSource &&
            !string.IsNullOrWhiteSpace(location.SourceTree?.FilePath) &&
            _snapshotFiles.ContainsKey(RelativePath(
                _root,
                location.SourceTree!.FilePath)));

        public void AddEdge(string kind, string fromNodeId, string toNodeId)
        {
            var id = CSharpSymbolGraphFingerprint.StableEdgeId(kind, fromNodeId, toNodeId);
            if (_edges.ContainsKey(id))
                return;
            if (_edges.Count >= _limits.MaxEdges)
                ThrowLimit("edge count", _limits.MaxEdges);
            var edge = new CSharpSymbolGraphEdge
            {
                Id = id,
                Kind = kind,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId
            };
            edge = new CSharpSymbolGraphEdge
            {
                Id = edge.Id,
                Hash = CSharpSymbolGraphFingerprint.CreateEdge(edge),
                Kind = edge.Kind,
                FromNodeId = edge.FromNodeId,
                ToNodeId = edge.ToNodeId
            };
            _edges.Add(id, edge);
            AddMemory(edge);
        }

        public void AddWorkspaceDiagnostic(
            string severity,
            string message,
            string projectPath,
            bool isFailure,
            string code = "AECSROSWORKSPACE")
        {
            _loadFailure |= isFailure;
            AddDiagnostic(new CSharpSymbolGraphDiagnostic
            {
                Source = "workspace",
                Severity = severity,
                Code = code,
                Message = NormalizeMessage(message),
                ProjectPath = projectPath
            });
        }

        public void AddCompilerDiagnostic(string projectPath, Diagnostic diagnostic)
        {
            var span = diagnostic.Location.IsInSource
                ? diagnostic.Location.GetLineSpan()
                : default;
            var filePath = diagnostic.Location.IsInSource &&
                !string.IsNullOrWhiteSpace(span.Path)
                ? CanonicalFilePath(RelativePath(_root, span.Path))
                : string.Empty;
            AddDiagnostic(new CSharpSymbolGraphDiagnostic
            {
                Source = "compiler",
                Severity = diagnostic.Severity.ToString(),
                Code = diagnostic.Id,
                Message = NormalizeMessage(diagnostic.GetMessage(CultureInfo.InvariantCulture)),
                ProjectPath = projectPath,
                FilePath = filePath,
                Line = diagnostic.Location.IsInSource
                    ? span.StartLinePosition.Line + 1
                    : 0,
                Column = diagnostic.Location.IsInSource
                    ? span.StartLinePosition.Character + 1
                    : 0
            });
        }

        public void CheckLimits()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_estimatedMemory > _limits.MaxEstimatedMemoryBytes)
                ThrowLimit("estimated memory", _limits.MaxEstimatedMemoryBytes);
        }

        public CSharpSymbolGraph CreateGraph(
            string compilerVersion,
            string msBuildVersion,
            string sdkVersion,
            IEnumerable<KeyValuePair<string, string>> globalProperties)
        {
            var graph = new CSharpSymbolGraph
            {
                RepositorySnapshotHash = _snapshot.SnapshotHash,
                BaselineCommit = _snapshot.BaselineCommit,
                CompilerVersion = compilerVersion,
                MsBuildVersion = msBuildVersion,
                SdkVersion = sdkVersion,
                LoadSucceeded = !_loadFailure && _projects.Count > 0,
                Limits = _limits,
                GlobalProperties = globalProperties.Select(property =>
                    new CSharpSymbolGraphOption
                    {
                        Name = property.Key,
                        Value = property.Value
                    }).ToList(),
                Projects = _projects.Values.OrderBy(project => project.Path, StringComparer.Ordinal)
                    .ToList(),
                Nodes = _nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
                Edges = _edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToList(),
                Diagnostics = _diagnostics.Values.OrderBy(
                        diagnostic => diagnostic.ProjectPath,
                        StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.FilePath, StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.Line)
                    .ThenBy(diagnostic => diagnostic.Column)
                    .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                    .ToList()
            };
            return new CSharpSymbolGraph
            {
                SchemaVersion = graph.SchemaVersion,
                StrategyVersion = graph.StrategyVersion,
                GraphHash = CSharpSymbolGraphFingerprint.Create(graph),
                RepositorySnapshotHash = graph.RepositorySnapshotHash,
                BaselineCommit = graph.BaselineCommit,
                CompilerVersion = graph.CompilerVersion,
                MsBuildVersion = graph.MsBuildVersion,
                SdkVersion = graph.SdkVersion,
                LoadSucceeded = graph.LoadSucceeded,
                Limits = graph.Limits,
                GlobalProperties = graph.GlobalProperties,
                Projects = graph.Projects,
                Nodes = graph.Nodes,
                Edges = graph.Edges,
                Diagnostics = graph.Diagnostics
            };
        }

        private void AddNode(CSharpSymbolGraphNode node)
        {
            if (_nodes.TryGetValue(node.Id, out var existing))
            {
                if ((existing.IsExternal && !node.IsExternal) ||
                    node.FilePaths.Count > existing.FilePaths.Count)
                {
                    _nodes[node.Id] = node;
                    AddMemory(node);
                }
                return;
            }
            if (_nodes.Count >= _limits.MaxNodes)
                ThrowLimit("node count", _limits.MaxNodes);
            _nodes.Add(node.Id, node);
            AddMemory(node);
        }

        private void AddDiagnostic(CSharpSymbolGraphDiagnostic diagnostic)
        {
            diagnostic = new CSharpSymbolGraphDiagnostic
            {
                Id = CSharpSymbolGraphFingerprint.StableDiagnosticId(diagnostic),
                Source = diagnostic.Source,
                Severity = diagnostic.Severity,
                Code = diagnostic.Code,
                Message = diagnostic.Message,
                ProjectPath = diagnostic.ProjectPath,
                FilePath = diagnostic.FilePath,
                Line = diagnostic.Line,
                Column = diagnostic.Column
            };
            if (_diagnostics.ContainsKey(diagnostic.Id))
                return;
            if (_diagnostics.Count >= _limits.MaxDiagnostics)
                ThrowLimit("diagnostic count", _limits.MaxDiagnostics);
            _diagnostics.Add(diagnostic.Id, diagnostic);
            AddMemory(diagnostic);
        }

        private string NormalizeMessage(string message)
        {
            var normalized = message.Replace(_root, "<repo>", StringComparison.OrdinalIgnoreCase)
                .Replace('\\', '/').Trim();
            return AbsolutePathRegex().Replace(normalized, "<path>");
        }

        private void AddMemory(object value)
        {
            _estimatedMemory = checked(_estimatedMemory +
                JsonSerializer.Serialize(value).Length * sizeof(char) + 128L);
            CheckLimits();
        }

        private string CanonicalFilePath(string candidate) =>
            _snapshotFiles.TryGetValue(candidate, out var file) && file.Language == "C#"
                ? file.Path
                : string.Empty;

        private static string ContainingNodeId(
            ISymbol symbol,
            string projectPath,
            string assemblyName)
        {
            if (symbol.ContainingType is not null)
                return SymbolNodeId(symbol.ContainingType, projectPath, assemblyName);
            if (symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
                return SymbolNodeId(containingNamespace, projectPath, assemblyName);
            return string.IsNullOrWhiteSpace(projectPath)
                ? string.Empty
                : CSharpSymbolGraphFingerprint.StableNodeId($"project|{projectPath}");
        }

        private static string SymbolNodeId(
            ISymbol symbol,
            string projectPath,
            string assemblyName)
        {
            var kind = symbol.Kind switch
            {
                SymbolKind.Namespace => "namespace",
                SymbolKind.NamedType => "type",
                _ => "member"
            };
            var documentationId = DocumentationCommentId.CreateDeclarationId(symbol) ?? string.Empty;
            var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            return CSharpSymbolGraphFingerprint.StableNodeId(
                $"{kind}|{projectPath}|{assemblyName}|" +
                (string.IsNullOrWhiteSpace(documentationId) ? display : documentationId));
        }

        private static List<string> Modifiers(ISymbol symbol)
        {
            var modifiers = new List<string>();
            if (symbol.IsStatic) modifiers.Add("static");
            if (symbol.IsAbstract) modifiers.Add("abstract");
            if (symbol.IsSealed) modifiers.Add("sealed");
            if (symbol.IsVirtual) modifiers.Add("virtual");
            if (symbol.IsOverride) modifiers.Add("override");
            if (symbol.IsExtern) modifiers.Add("extern");
            if (symbol is IMethodSymbol { IsAsync: true }) modifiers.Add("async");
            if (symbol.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax().ChildTokens().Any(token =>
                        token.IsKind(SyntaxKind.PartialKeyword))))
            {
                modifiers.Add("partial");
            }
            var typeParameters = symbol switch
            {
                INamedTypeSymbol type => type.TypeParameters,
                IMethodSymbol method => method.TypeParameters,
                _ => ImmutableArray<ITypeParameterSymbol>.Empty
            };
            foreach (var parameter in typeParameters)
            {
                var constraints = new List<string>();
                if (parameter.HasReferenceTypeConstraint)
                {
                    constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation ==
                        NullableAnnotation.Annotated ? "class?" : "class");
                }
                if (parameter.HasValueTypeConstraint) constraints.Add("struct");
                if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
                if (parameter.HasNotNullConstraint) constraints.Add("notnull");
                constraints.AddRange(parameter.ConstraintTypes.Select(type =>
                    type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
                if (parameter.HasConstructorConstraint) constraints.Add("new()");
                if (constraints.Count > 0)
                {
                    modifiers.Add(
                        $"constraint:{parameter.Name}:" +
                        string.Join("&", constraints.OrderBy(value => value, StringComparer.Ordinal)));
                }
            }
            return modifiers.OrderBy(item => item, StringComparer.Ordinal).ToList();
        }

        private static CSharpSymbolGraphNode WithNodeHash(CSharpSymbolGraphNode node) => new()
        {
            Id = node.Id,
            Hash = CSharpSymbolGraphFingerprint.CreateNode(node),
            Kind = node.Kind,
            Name = node.Name,
            DisplayName = node.DisplayName,
            DocumentationId = node.DocumentationId,
            ProjectPath = node.ProjectPath,
            FilePaths = node.FilePaths,
            SourceHash = node.SourceHash,
            ContainingNodeId = node.ContainingNodeId,
            Accessibility = node.Accessibility,
            Modifiers = node.Modifiers,
            Arity = node.Arity,
            TypeKind = node.TypeKind,
            MemberKind = node.MemberKind,
            AssemblyName = node.AssemblyName,
            IsExternal = node.IsExternal
        };

        private static void ThrowLimit(string name, long limit) =>
            throw new InvalidOperationException(
                $"C# symbol graph exceeded the configured {name} limit ({limit}).");

        [GeneratedRegex(@"(?<![\w.])(?:[a-zA-Z]:/|/)[^\s,;]+", RegexOptions.CultureInvariant)]
        private static partial Regex AbsolutePathRegex();
    }
}
