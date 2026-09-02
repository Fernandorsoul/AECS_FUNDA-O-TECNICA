using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.RepositorySnapshots;

public sealed partial class RepositorySnapshotBuilder
{
    private const long MaximumParsedManifestBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] DefaultExcludedDirectoryNames =
    [
        ".git", ".vs", ".idea", ".vscode", ".aecs-verification", "artifacts",
        "bin", "build", "coverage", "dist", "node_modules", "obj", "packages",
        "TestResults"
    ];
    private static readonly HashSet<string> ProjectExtensions = new(
        [".csproj", ".fsproj", ".vbproj"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SolutionExtensions = new(
        [".sln", ".slnx"],
        StringComparer.OrdinalIgnoreCase);
    private readonly IProcessRunner _processRunner;

    public RepositorySnapshotBuilder(IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
    }

    public async Task<RepositorySnapshot> BuildAsync(
        string workspacePath,
        string baselineCommit,
        TaskContract contract,
        IReadOnlyCollection<ExecutionCommandEvidence> baselineCommands,
        CancellationToken cancellationToken)
    {
        return await BuildTreeAsync(
            workspacePath,
            baselineCommit,
            baselineCommit,
            contract,
            baselineCommands,
            cancellationToken);
    }

    public async Task<RepositorySnapshot> BuildCandidateAsync(
        string workspacePath,
        string baselineCommit,
        TaskContract contract,
        IReadOnlyCollection<ExecutionCommandEvidence> baselineCommands,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspacePath);
        var candidateTree = await WriteCandidateTreeAsync(root, cancellationToken);
        return await BuildTreeAsync(
            root,
            candidateTree,
            baselineCommit,
            contract,
            baselineCommands,
            cancellationToken);
    }

    private async Task<RepositorySnapshot> BuildTreeAsync(
        string workspacePath,
        string treeish,
        string baselineCommit,
        TaskContract contract,
        IReadOnlyCollection<ExecutionCommandEvidence> baselineCommands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(baselineCommands);
        var root = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository snapshot root not found: {root}");
        if (!GitObjectIdRegex().IsMatch(baselineCommit))
            throw new InvalidOperationException("Repository snapshot requires a valid baseline commit.");

        var profile = contract.Execution.RepositorySnapshot ?? new RepositorySnapshotProfile();
        if (profile.Version != RepositorySnapshotSchema.ProfileVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported repository snapshot profile: '{profile.Version}'.");
        }
        var customExclusions = NormalizeConfiguredExclusions(profile.ExcludedDirectories);
        var effectiveExclusions = DefaultExcludedDirectoryNames
            .Concat(customExclusions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (!GitObjectIdRegex().IsMatch(treeish))
            throw new InvalidOperationException("Repository snapshot requires a valid Git tree.");

        var tree = await ReadGitTreeAsync(root, treeish, cancellationToken);
        var included = new List<TreeEntry>();
        var excludedEntryCount = 0;
        foreach (var entry in tree)
        {
            if (entry.Type != "blob" || entry.Mode == "120000" ||
                ShouldExclude(entry.Path, customExclusions) || IsSensitive(entry.Path) ||
                IsArtifact(entry.Path))
            {
                excludedEntryCount++;
                continue;
            }
            included.Add(entry);
        }

        var files = included.Select(entry => new RepositorySnapshotFile
            {
                Path = entry.Path,
                Hash = $"git:{entry.ObjectId}",
                Size = entry.Size,
                Kind = FileKind(entry.Path),
                Language = Language(entry.Path)
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var entriesByPath = included.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var projects = included
            .Where(entry => ProjectExtensions.Contains(Path.GetExtension(entry.Path)))
            .Select(entry => ReadProject(root, entry))
            .OrderBy(project => project.Path, StringComparer.Ordinal)
            .ToList();
        var packages = projects.SelectMany(project => ReadProjectPackages(root, project.Path))
            .Concat(ReadManifestPackages(root, included))
            .DistinctBy(
                package => $"{package.Ecosystem}\n{package.Name}\n{package.Version}\n{package.SourcePath}",
                StringComparer.Ordinal)
            .OrderBy(package => package.Ecosystem, StringComparer.Ordinal)
            .ThenBy(package => package.Name, StringComparer.Ordinal)
            .ThenBy(package => package.Version, StringComparer.Ordinal)
            .ThenBy(package => package.SourcePath, StringComparer.Ordinal)
            .ToList();
        var solutions = included
            .Where(entry => SolutionExtensions.Contains(Path.GetExtension(entry.Path)))
            .Select(entry => ReadSolution(root, entry))
            .OrderBy(solution => solution.Path, StringComparer.Ordinal)
            .ToList();
        var relationships = projects.SelectMany(project => project.ProjectReferences.Select(reference =>
                new RepositorySnapshotRelationship
                {
                    From = project.Path,
                    To = reference,
                    Kind = "project-reference"
                }))
            .Concat(solutions.SelectMany(solution => solution.Projects.Select(project =>
                new RepositorySnapshotRelationship
                {
                    From = solution.Path,
                    To = project,
                    Kind = "solution-project"
                })))
            .DistinctBy(
                relation => $"{relation.Kind}\n{relation.From}\n{relation.To}",
                StringComparer.Ordinal)
            .OrderBy(relation => relation.Kind, StringComparer.Ordinal)
            .ThenBy(relation => relation.From, StringComparer.Ordinal)
            .ThenBy(relation => relation.To, StringComparer.Ordinal)
            .ToList();
        var manifests = included
            .Where(entry => IsManifest(entry.Path))
            .Select(entry => new RepositorySnapshotManifest
            {
                Path = entry.Path,
                Hash = $"git:{entry.ObjectId}",
                Kind = ManifestKind(entry.Path)
            })
            .OrderBy(manifest => manifest.Path, StringComparer.Ordinal)
            .ToList();
        var languages = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Language))
            .GroupBy(file => file.Language, StringComparer.Ordinal)
            .Select(group => new RepositorySnapshotLanguage
            {
                Name = group.Key,
                FileCount = group.Count()
            })
            .OrderBy(language => language.Name, StringComparer.Ordinal)
            .ToList();
        var frameworks = projects.SelectMany(project => project.Frameworks)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .ToList();
        var entrypoints = DiscoverEntrypoints(files, projects);
        var testSuites = DiscoverTestSuites(contract, projects);
        var tools = DiscoverTools(baselineCommands);
        var configurationHash = RepositorySnapshotFingerprint.CreateConfiguration(
            profile.Version,
            effectiveExclusions);
        var snapshot = new RepositorySnapshot
        {
            ConfigurationHash = configurationHash,
            ProfileVersion = profile.Version,
            BaselineCommit = baselineCommit,
            TaskContractId = contract.Id,
            ExcludedEntryCount = excludedEntryCount,
            ExcludedDirectories = effectiveExclusions,
            Tools = tools,
            Files = files,
            Solutions = solutions,
            Projects = projects,
            Languages = languages,
            Frameworks = frameworks,
            Manifests = manifests,
            Packages = packages,
            Entrypoints = entrypoints,
            TestSuites = testSuites,
            Relationships = relationships
        };
        return WithHash(snapshot, RepositorySnapshotFingerprint.Create(snapshot));
    }

    private async Task<string> WriteCandidateTreeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = ["write-tree"],
            WorkingDirectory = root,
            Timeout = GitTimeout
        }, cancellationToken);
        var tree = result.StandardOutput.Trim();
        if (!result.Succeeded || !GitObjectIdRegex().IsMatch(tree))
        {
            throw new InvalidOperationException(
                $"Could not materialize candidate Git tree: exit code {result.ExitCode}; " +
                result.StandardError.Trim());
        }
        return tree;
    }

    private async Task<List<TreeEntry>> ReadGitTreeAsync(
        string root,
        string treeish,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = ["ls-tree", "-r", "-l", "-z", "--full-tree", treeish],
            WorkingDirectory = root,
            Timeout = GitTimeout
        }, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not inventory Git tree: exit code {result.ExitCode}; " +
                result.StandardError.Trim());
        }

        var entries = new List<TreeEntry>();
        foreach (var record in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\t');
            if (separator <= 0 || separator == record.Length - 1)
                throw new InvalidOperationException("Git returned a malformed tree entry.");
            var fields = record[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4)
            {
                throw new InvalidOperationException("Git returned an unsupported tree entry.");
            }
            var size = fields[3] == "-"
                ? 0
                : long.TryParse(
                    fields[3],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedSize)
                    ? parsedSize
                    : throw new InvalidOperationException("Git returned an invalid tree entry size.");
            var path = NormalizeGitPath(record[(separator + 1)..]);
            entries.Add(new TreeEntry(fields[0], fields[1], fields[2], size, path));
        }
        return entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
    }

    private static RepositorySnapshotProject ReadProject(string root, TreeEntry entry)
    {
        var document = ReadXml(root, entry.Path);
        var packageNames = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => Attribute(element, "Include") ?? Attribute(element, "Update"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();
        var references = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => Attribute(element, "Include"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolveRepositoryRelativePath(root, entry.Path, path!))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var frameworks = document.Descendants()
            .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(element => element.Value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .ToList();
        var explicitTestProject = document.Descendants()
            .Any(element => element.Name.LocalName == "IsTestProject" &&
                bool.TryParse(element.Value.Trim(), out var value) && value);
        var isTestProject = explicitTestProject ||
            packageNames.Any(IsTestPackage) || LooksLikeTestPath(entry.Path);
        return new RepositorySnapshotProject
        {
            Path = entry.Path,
            Hash = $"git:{entry.ObjectId}",
            Language = ProjectLanguage(entry.Path),
            Sdk = document.Root?.Attribute("Sdk")?.Value.Trim() ?? string.Empty,
            OutputType = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "OutputType")
                ?.Value.Trim() ?? string.Empty,
            IsTestProject = isTestProject,
            Frameworks = frameworks,
            ProjectReferences = references,
            PackageReferences = packageNames
        };
    }

    private static IEnumerable<RepositorySnapshotPackage> ReadProjectPackages(
        string root,
        string projectPath)
    {
        var document = ReadXml(root, projectPath);
        foreach (var package in document.Descendants()
                     .Where(element => element.Name.LocalName == "PackageReference"))
        {
            var name = Attribute(package, "Include") ?? Attribute(package, "Update");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var version = Attribute(package, "Version") ?? package.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Version")?.Value ?? string.Empty;
            yield return new RepositorySnapshotPackage
            {
                Ecosystem = "nuget",
                Name = name.Trim(),
                Version = version.Trim(),
                SourcePath = projectPath
            };
        }
    }

    private static IEnumerable<RepositorySnapshotPackage> ReadManifestPackages(
        string root,
        IEnumerable<TreeEntry> entries)
    {
        foreach (var entry in entries.Where(entry =>
                     Path.GetFileName(entry.Path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)))
        {
            var document = ReadXml(root, entry.Path);
            foreach (var package in document.Descendants()
                         .Where(element => element.Name.LocalName == "PackageVersion"))
            {
                var name = Attribute(package, "Include") ?? Attribute(package, "Update");
                var version = Attribute(package, "Version") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return new RepositorySnapshotPackage
                    {
                        Ecosystem = "nuget",
                        Name = name.Trim(),
                        Version = version.Trim(),
                        SourcePath = entry.Path
                    };
                }
            }
        }

        foreach (var entry in entries.Where(entry =>
                     Path.GetFileName(entry.Path).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            using var document = JsonDocument.Parse(ReadText(root, entry.Path), new JsonDocumentOptions
            {
                MaxDepth = 32,
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            foreach (var sectionName in new[]
                     {
                         "dependencies", "devDependencies", "peerDependencies", "optionalDependencies"
                     })
            {
                if (!document.RootElement.TryGetProperty(sectionName, out var section) ||
                    section.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var dependency in section.EnumerateObject())
                {
                    yield return new RepositorySnapshotPackage
                    {
                        Ecosystem = "npm",
                        Name = dependency.Name,
                        Version = SafePackageVersion(dependency.Value.ValueKind == JsonValueKind.String
                            ? dependency.Value.GetString() ?? string.Empty
                            : string.Empty),
                        SourcePath = entry.Path
                    };
                }
            }
        }
    }

    private static RepositorySnapshotSolution ReadSolution(string root, TreeEntry entry)
    {
        var projects = Path.GetExtension(entry.Path).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ReadSlnxProjects(root, entry.Path)
            : ReadSlnProjects(root, entry.Path);
        return new RepositorySnapshotSolution
        {
            Path = entry.Path,
            Hash = $"git:{entry.ObjectId}",
            Projects = projects
        };
    }

    private static List<string> ReadSlnxProjects(string root, string solutionPath) =>
        ReadXml(root, solutionPath).Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => Attribute(element, "Path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolveRepositoryRelativePath(root, solutionPath, path!))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static List<string> ReadSlnProjects(string root, string solutionPath) =>
        SlnProjectRegex().Matches(ReadText(root, solutionPath))
            .Select(match => match.Groups[1].Value)
            .Where(path => ProjectExtensions.Contains(Path.GetExtension(path)))
            .Select(path => ResolveRepositoryRelativePath(root, solutionPath, path))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static List<RepositorySnapshotEntrypoint> DiscoverEntrypoints(
        IReadOnlyCollection<RepositorySnapshotFile> files,
        IReadOnlyCollection<RepositorySnapshotProject> projects)
    {
        var candidates = files.Where(file => EntrypointKind(file.Path) is not null).ToList();
        return candidates.Select(file => new RepositorySnapshotEntrypoint
            {
                Path = file.Path,
                Language = file.Language,
                Kind = EntrypointKind(file.Path)!,
                ProjectPath = FindOwningProject(file.Path, projects)
            })
            .OrderBy(entrypoint => entrypoint.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static List<RepositorySnapshotTestSuite> DiscoverTestSuites(
        TaskContract contract,
        IReadOnlyCollection<RepositorySnapshotProject> projects)
    {
        var suites = new List<RepositorySnapshotTestSuite>();
        if (contract.Execution.TestSuites is not null)
        {
            foreach (var configured in new[]
                     {
                         new ConfiguredTestSuite(TestSuiteCategory.Unit, contract.Execution.TestSuites.Unit),
                         new ConfiguredTestSuite(TestSuiteCategory.Integration, contract.Execution.TestSuites.Integration),
                         new ConfiguredTestSuite(TestSuiteCategory.Acceptance, contract.Execution.TestSuites.Acceptance)
                     })
            {
                suites.Add(new RepositorySnapshotTestSuite
                {
                    Name = configured.Category + "Tests",
                    Category = configured.Category.ToString(),
                    Target = ResolveExecutionTarget(contract.Execution, configured.Profile.Target),
                    Mode = configured.Profile.Mode.ToString(),
                    Source = "task-contract"
                });
            }
        }
        else if (contract.Verification.UnitTests || contract.Verification.IntegrationTests)
        {
            suites.Add(new RepositorySnapshotTestSuite
            {
                Name = "Tests",
                Category = "Aggregate",
                Target = ResolveExecutionTarget(contract.Execution, contract.Execution.Target),
                Mode = "Required",
                Source = "legacy-task-contract"
            });
        }

        var configuredTargets = suites.Select(suite => suite.Target)
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.Where(project => project.IsTestProject &&
                     !configuredTargets.Contains(project.Path)))
        {
            suites.Add(new RepositorySnapshotTestSuite
            {
                Name = Path.GetFileNameWithoutExtension(project.Path),
                Category = InferTestCategory(project.Path),
                Target = project.Path,
                Mode = "Discovered",
                Source = "project-inventory"
            });
        }
        return suites.OrderBy(suite => suite.Source, StringComparer.Ordinal)
            .ThenBy(suite => suite.Category, StringComparer.Ordinal)
            .ThenBy(suite => suite.Target, StringComparer.Ordinal)
            .ToList();
    }

    private static List<RepositorySnapshotTool> DiscoverTools(
        IEnumerable<ExecutionCommandEvidence> baselineCommands) => baselineCommands
        .Where(command => command.Arguments.SequenceEqual(["--version"], StringComparer.Ordinal))
        .Select(command => new RepositorySnapshotTool
        {
            Name = command.FileName,
            Version = command.StandardOutput.Trim(),
            Available = command.ExitCode == 0 && !command.TimedOut && !command.Cancelled,
            Runtime = command.Environment?.Runtime ?? string.Empty,
            RuntimeVersion = command.Environment?.RuntimeVersion ?? string.Empty,
            ImageDigest = command.Environment?.ImageDigest ?? string.Empty
        })
        .OrderBy(tool => tool.Name, StringComparer.Ordinal)
        .ToList();

    private static RepositorySnapshot WithHash(RepositorySnapshot snapshot, string hash) => new()
    {
        SchemaVersion = snapshot.SchemaVersion,
        StrategyVersion = snapshot.StrategyVersion,
        SnapshotHash = hash,
        ConfigurationHash = snapshot.ConfigurationHash,
        ProfileVersion = snapshot.ProfileVersion,
        BaselineCommit = snapshot.BaselineCommit,
        TaskContractId = snapshot.TaskContractId,
        ExcludedEntryCount = snapshot.ExcludedEntryCount,
        ExcludedDirectories = snapshot.ExcludedDirectories,
        Tools = snapshot.Tools,
        Files = snapshot.Files,
        Solutions = snapshot.Solutions,
        Projects = snapshot.Projects,
        Languages = snapshot.Languages,
        Frameworks = snapshot.Frameworks,
        Manifests = snapshot.Manifests,
        Packages = snapshot.Packages,
        Entrypoints = snapshot.Entrypoints,
        TestSuites = snapshot.TestSuites,
        Relationships = snapshot.Relationships
    };

    private static List<string> NormalizeConfiguredExclusions(IEnumerable<string> directories) =>
        directories.Select(NormalizeConfiguredExclusion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(directory => directory, StringComparer.Ordinal)
            .ToList();

    private static string NormalizeConfiguredExclusion(string directory)
    {
        var normalized = directory.Trim().Replace('\\', '/').TrimEnd('/');
        var segments = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') ||
            Regex.IsMatch(normalized, @"^[a-zA-Z]:/") ||
            segments.Any(segment => segment is "" or "." or ".." ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Repository snapshot exclusion must be a safe relative directory: '{directory}'.");
        }
        return normalized;
    }

    private static bool ShouldExclude(string path, IReadOnlyCollection<string> customExclusions)
    {
        var segments = path.Split('/');
        if (segments.Take(Math.Max(0, segments.Length - 1)).Any(segment =>
                DefaultExcludedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }
        return customExclusions.Any(directory =>
            path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSensitive(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".pypirc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".p12", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".key", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jks", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsArtifact(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".class" or ".coverage" or ".dll" or ".dylib" or ".exe" or ".jar" or ".nupkg" or
        ".o" or ".obj" or ".pdb" or ".pyc" or ".snupkg" or ".so" or ".trx";

    private static string SafePackageVersion(string value)
    {
        var normalized = value.Trim();
        return normalized.Contains("://", StringComparison.Ordinal) ||
            normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase)
            ? "[non-version-specifier]"
            : normalized;
    }

    private static string NormalizeGitPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') ||
            segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException("Git returned an unsafe repository path.");
        }
        return normalized;
    }

    private static string FileKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (ProjectExtensions.Contains(extension))
            return "project";
        if (SolutionExtensions.Contains(extension))
            return "solution";
        if (IsManifest(path))
            return "manifest";
        if (!string.IsNullOrWhiteSpace(Language(path)))
            return LooksLikeTestPath(path) ? "test-source" : "source";
        return "other";
    }

    private static string Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".fs" => "F#",
        ".vb" => "Visual Basic",
        ".ts" or ".tsx" => "TypeScript",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "JavaScript",
        ".py" => "Python",
        ".go" => "Go",
        ".rs" => "Rust",
        ".java" => "Java",
        ".kt" or ".kts" => "Kotlin",
        ".c" or ".h" => "C",
        ".cc" or ".cpp" or ".cxx" or ".hpp" => "C++",
        ".sql" => "SQL",
        ".sh" => "Shell",
        ".ps1" => "PowerShell",
        ".html" => "HTML",
        ".css" or ".scss" or ".sass" => "CSS",
        _ => string.Empty
    };

    private static string ProjectLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csproj" => "C#",
        ".fsproj" => "F#",
        ".vbproj" => "Visual Basic",
        _ => string.Empty
    };

    private static bool IsManifest(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("go.sum", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("docker-compose.", StringComparison.OrdinalIgnoreCase);
    }

    private static string ManifestKind(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name is "package.json" or "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock")
            return "npm";
        if (name is "pyproject.toml" || name.StartsWith("requirements"))
            return "python";
        if (name is "go.mod" or "go.sum")
            return "go";
        if (name is "cargo.toml" or "cargo.lock")
            return "cargo";
        if (name is "pom.xml" or "build.gradle" or "build.gradle.kts")
            return "jvm";
        if (name is "dockerfile" || name.StartsWith("docker-compose."))
            return "container";
        return "dotnet";
    }

    private static bool LooksLikeTestPath(string path) => path.Split('/').Any(segment =>
        segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        segment.Contains("Tests", StringComparison.OrdinalIgnoreCase));

    private static bool IsTestPackage(string name) =>
        name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase);

    private static string? EntrypointKind(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name switch
        {
            "program.cs" or "startup.cs" => "dotnet-entrypoint",
            "main.py" or "__main__.py" or "app.py" or "manage.py" => "python-entrypoint",
            "index.js" or "index.ts" or "server.js" or "server.ts" => "node-entrypoint",
            "main.go" => "go-entrypoint",
            "main.rs" => "rust-entrypoint",
            _ => null
        };
    }

    private static string FindOwningProject(
        string filePath,
        IEnumerable<RepositorySnapshotProject> projects) => projects
        .Select(project => new
        {
            Project = project.Path,
            Directory = (Path.GetDirectoryName(project.Path) ?? string.Empty).Replace('\\', '/')
        })
        .Where(candidate => string.IsNullOrEmpty(candidate.Directory) ||
            filePath.StartsWith(candidate.Directory + "/", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(candidate => candidate.Directory.Length)
        .Select(candidate => candidate.Project)
        .FirstOrDefault() ?? string.Empty;

    private static string InferTestCategory(string path)
    {
        if (path.Contains("integration", StringComparison.OrdinalIgnoreCase))
            return "Integration";
        if (path.Contains("acceptance", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("e2e", StringComparison.OrdinalIgnoreCase))
        {
            return "Acceptance";
        }
        return "Unit";
    }

    private static string ResolveExecutionTarget(
        RepositoryExecutionProfile execution,
        string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return string.Empty;
        var workingDirectory = execution.WorkingDirectory.Trim().Replace('\\', '/').Trim('/');
        var normalizedTarget = target.Trim().Replace('\\', '/');
        var combined = string.IsNullOrWhiteSpace(workingDirectory) || workingDirectory == "."
            ? normalizedTarget
            : $"{workingDirectory}/{normalizedTarget}";
        var segments = combined.Split('/');
        if (Path.IsPathRooted(combined) || combined.StartsWith('/') ||
            Regex.IsMatch(combined, @"^[a-zA-Z]:/") ||
            segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException(
                $"Repository snapshot test target is unsafe: '{combined}'.");
        }
        return combined;
    }

    private static XDocument ReadXml(string root, string path)
    {
        using var reader = XmlReader.Create(
            new StringReader(ReadText(root, path)),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0
            });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string ReadText(string root, string path)
    {
        var fullPath = ResolveSafeFile(root, path);
        var info = new FileInfo(fullPath);
        if (info.Length > MaximumParsedManifestBytes)
        {
            throw new InvalidOperationException(
                $"Repository manifest exceeds the safe parsing limit: '{path}'.");
        }
        return File.ReadAllText(fullPath);
    }

    private static string ResolveSafeFile(string root, string path)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison) || !File.Exists(fullPath))
            throw new InvalidOperationException($"Repository manifest is unavailable: '{path}'.");

        var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in path.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Repository manifest crosses an unsafe symbolic link: '{path}'.");
            }
        }
        return fullPath;
    }

    private static string? ResolveRepositoryRelativePath(
        string root,
        string sourcePath,
        string referencedPath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath.Replace('/', Path.DirectorySeparatorChar))
            ?? string.Empty;
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            sourceDirectory,
            referencedPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
            return null;
        return Path.GetRelativePath(root, candidate).Replace('\\', '/');
    }

    private static string? Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private sealed record TreeEntry(
        string Mode,
        string Type,
        string ObjectId,
        long Size,
        string Path);

    [GeneratedRegex("^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitObjectIdRegex();

    [GeneratedRegex("Project\\([^\\r\\n]*?\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex SlnProjectRegex();
}
