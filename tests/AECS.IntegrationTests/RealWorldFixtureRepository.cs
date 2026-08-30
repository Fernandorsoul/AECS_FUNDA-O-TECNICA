using AECS.Domain.Models;
using AECS.Infrastructure.Processes;

namespace AECS.IntegrationTests;

internal sealed class RealWorldFixtureRepository : IAsyncDisposable
{
    private const string TemporaryRootPrefix = "aecs-real-world-e2e-";
    private bool _disposed;

    private RealWorldFixtureRepository(string rootPath, string evidencePath)
    {
        RootPath = rootPath;
        Path = System.IO.Path.Combine(rootPath, "repository");
        EvidencePath = evidencePath;
    }

    public static string FixturePath => System.IO.Path.GetFullPath(System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "tests", "fixtures", "real-world-demo"));

    public string RootPath { get; }
    public string Path { get; }
    public string EvidencePath { get; }
    public SystemProcessRunner ProcessRunner { get; } = new();

    public static async Task<RealWorldFixtureRepository> CreateAsync(string? evidencePath = null)
    {
        if (!Directory.Exists(FixturePath))
            throw new DirectoryNotFoundException($"Versioned real-world fixture not found: {FixturePath}");

        var rootPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{TemporaryRootPrefix}{Guid.NewGuid():N}");
        var repository = new RealWorldFixtureRepository(
            rootPath,
            evidencePath ?? System.IO.Path.Combine(rootPath, "evidence"));
        CopyDirectory(System.IO.Path.Combine(FixturePath, "repository"), repository.Path);

        await repository.GitAsync("init", "--initial-branch=fixture");
        await repository.GitAsync("config", "user.email", "aecs-tests@example.invalid");
        await repository.GitAsync("config", "user.name", "AECS Tests");
        await repository.GitAsync("config", "core.autocrlf", "false");
        await repository.GitAsync("add", "-A", "--");
        await repository.GitAsync("commit", "-m", "AgronomoPlus reproducible baseline");
        return repository;
    }

    public async Task<RealWorldRepositorySnapshot> SnapshotAsync() => new()
    {
        Commit = (await GitAsync("rev-parse", "HEAD")).StandardOutput.Trim(),
        Branch = (await GitAsync("rev-parse", "--abbrev-ref", "HEAD")).StandardOutput.Trim(),
        Status = (await GitAsync("status", "--porcelain=v1", "--untracked-files=all"))
            .StandardOutput,
        Worktrees = await WorktreesAsync()
    };

    public async Task<IReadOnlyList<string>> WorktreesAsync()
    {
        var output = (await GitAsync("worktree", "list", "--porcelain")).StandardOutput;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => System.IO.Path.GetFullPath(line["worktree ".Length..].Trim()))
            .ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Directory.Exists(System.IO.Path.Combine(Path, ".git")))
        {
            try
            {
                foreach (var worktree in await WorktreesAsync())
                {
                    if (SamePath(worktree, Path) ||
                        !System.IO.Path.GetFileName(worktree).StartsWith(
                            "aecs-staging-",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await GitAsync("worktree", "remove", "--force", worktree);
                }

                await GitAsync("worktree", "prune");
            }
            catch
            {
                // The assertions observe leaked worktrees before disposal. Cleanup below remains
                // best effort so a failed scenario does not pollute the developer or CI machine.
            }
        }

        var resolvedRoot = System.IO.Path.GetFullPath(RootPath);
        var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (!resolvedRoot.StartsWith(
                temporaryRoot + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(resolvedRoot).StartsWith(
                TemporaryRootPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to delete unexpected fixture path: {resolvedRoot}");
        }

        if (!Directory.Exists(resolvedRoot))
            return;

        foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(resolvedRoot, recursive: true);
    }

    private async Task<ProcessExecutionResult> GitAsync(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = Path,
            Timeout = TimeSpan.FromMinutes(2)
        }, CancellationToken.None);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with {result.ExitCode}: " +
                result.StandardError);
        }

        return result;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(System.IO.Path.Combine(
                destination,
                System.IO.Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(
                destination,
                System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(
        System.IO.Path.GetFullPath(left).TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar),
        System.IO.Path.GetFullPath(right).TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}

internal sealed class RealWorldRepositorySnapshot
{
    public string Commit { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<string> Worktrees { get; init; } = [];
}
