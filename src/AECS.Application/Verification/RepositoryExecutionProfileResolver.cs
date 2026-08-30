using System.Text.RegularExpressions;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public sealed class ResolvedRepositoryExecutionProfile
{
    public string WorkingDirectory { get; init; } = string.Empty;
    public string? TargetArgument { get; init; }

    public IReadOnlyList<string> BuildArguments => CreateArguments("build", noBuild: false);
    public IReadOnlyList<string> TestArguments => CreateArguments("test", noBuild: true);

    private IReadOnlyList<string> CreateArguments(string command, bool noBuild)
    {
        var arguments = new List<string> { command };
        if (!string.IsNullOrWhiteSpace(TargetArgument))
            arguments.Add(TargetArgument);
        if (noBuild)
            arguments.Add("--no-build");
        return arguments;
    }
}

public static partial class RepositoryExecutionProfileResolver
{
    private static readonly HashSet<string> AllowedTargetExtensions = new(
        [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"],
        StringComparer.OrdinalIgnoreCase);

    public static ResolvedRepositoryExecutionProfile Resolve(
        string workspacePath,
        RepositoryExecutionProfile profile)
    {
        var workspaceRoot = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace path not found: {workspaceRoot}");

        var workingDirectory = ResolveRelativePath(
            workspaceRoot,
            profile.WorkingDirectory,
            allowRoot: true,
            "working directory");
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Execution working directory not found: '{profile.WorkingDirectory}'.");
        }

        if (string.IsNullOrWhiteSpace(profile.Target))
            throw new InvalidOperationException("An explicit execution target is required.");

        var target = ResolveRelativePath(
            workingDirectory,
            profile.Target,
            allowRoot: false,
            "target");
        if (!File.Exists(target))
            throw new FileNotFoundException($"Execution target not found: '{profile.Target}'.");

        var extension = Path.GetExtension(target);
        if (!AllowedTargetExtensions.Contains(extension))
        {
            throw new InvalidOperationException(
                $"Execution target must be a supported solution or project file: '{profile.Target}'.");
        }

        var targetArgument = Path.GetRelativePath(workingDirectory, target)
            .Replace('\\', '/');

        return new ResolvedRepositoryExecutionProfile
        {
            WorkingDirectory = workingDirectory,
            TargetArgument = targetArgument
        };
    }

    private static string ResolveRelativePath(
        string root,
        string suppliedPath,
        bool allowRoot,
        string fieldName)
    {
        var normalized = string.IsNullOrWhiteSpace(suppliedPath)
            ? "."
            : suppliedPath.Trim().Replace('\\', '/');

        if (Path.IsPathRooted(suppliedPath) ||
            normalized.StartsWith('/') ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            WindowsAbsolutePathRegex().IsMatch(normalized))
        {
            throw new InvalidOperationException(
                $"Execution {fieldName} must be relative to the workspace: '{suppliedPath}'.");
        }

        if (allowRoot && normalized == ".")
            return Path.GetFullPath(root);

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Execution {fieldName} contains path traversal: '{suppliedPath}'.");
        }

        if (segments.Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Execution {fieldName} cannot reference Git metadata: '{suppliedPath}'.");
        }

        var resolvedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(resolvedRoot, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = resolvedRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException(
                $"Execution {fieldName} escapes the workspace: '{suppliedPath}'.");
        }

        var current = resolvedRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Execution {fieldName} crosses a symbolic link or junction: '{suppliedPath}'.");
            }
        }

        return candidate;
    }

    [GeneratedRegex(@"^[a-zA-Z]:[\\/]")]
    private static partial Regex WindowsAbsolutePathRegex();
}
