using System.Security.Cryptography;
using System.Text;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Staging;

public sealed class StagingWorkspace : IAsyncDisposable
{
    private readonly GitWorkspaceManager _manager;
    private bool _disposed;

    internal StagingWorkspace(
        GitWorkspaceManager manager,
        BaselineSnapshot baseline,
        string path)
    {
        _manager = manager;
        Baseline = baseline;
        Path = path;
    }

    public BaselineSnapshot Baseline { get; }
    public string Path { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _manager.RemoveWorkspaceAsync(this, CancellationToken.None);
    }
}

public sealed class GitWorkspaceManager
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _processRunner;

    public GitWorkspaceManager(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<BaselineSnapshot> CaptureBaselineAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var requestedPath = System.IO.Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(requestedPath))
            throw new DirectoryNotFoundException($"Repository path not found: {requestedPath}");

        var root = (await RunGitAsync(
            requestedPath,
            ["rev-parse", "--show-toplevel"],
            cancellationToken)).StandardOutput.Trim();
        root = System.IO.Path.GetFullPath(root);

        var status = (await RunGitAsync(
            root,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken)).StandardOutput;

        var baseline = new BaselineSnapshot
        {
            Commit = (await RunGitAsync(root, ["rev-parse", "HEAD"], cancellationToken))
                .StandardOutput.Trim(),
            Branch = (await RunGitAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken))
                .StandardOutput.Trim(),
            GitStatus = status,
            RepositoryPath = root,
            CapturedAt = DateTime.UtcNow
        };

        if (!baseline.IsClean)
        {
            throw new InvalidOperationException(
                $"Refusing to execute against a dirty repository. Baseline status:{Environment.NewLine}{status}");
        }

        return baseline;
    }

    public async Task<StagingWorkspace> CreateWorkspaceAsync(
        BaselineSnapshot baseline,
        CancellationToken cancellationToken)
    {
        if (!baseline.IsClean)
            throw new InvalidOperationException("Cannot create staging workspace from a dirty baseline.");

        var stagingPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"aecs-staging-{Guid.NewGuid():N}");

        await RunGitAsync(
            baseline.RepositoryPath,
            ["worktree", "add", "--detach", stagingPath, baseline.Commit],
            cancellationToken);

        return new StagingWorkspace(this, baseline, stagingPath);
    }

    public async Task<CandidateChangeSet> CreateCandidateAsync(
        StagingWorkspace workspace,
        string taskId,
        string agentRunId,
        CancellationToken cancellationToken)
    {
        // Staging the disposable worktree makes tracked and untracked changes
        // visible through one authoritative Git diff.
        await RunGitAsync(workspace.Path, ["add", "-A", "--"], cancellationToken);

        var nameStatus = (await RunGitAsync(
            workspace.Path,
            ["diff", "--cached", "--name-status", "-z", "--find-renames", workspace.Baseline.Commit, "--"],
            cancellationToken)).StandardOutput;
        var diff = (await RunGitAsync(
            workspace.Path,
            ["diff", "--cached", "--binary", "--no-ext-diff", workspace.Baseline.Commit, "--"],
            cancellationToken)).StandardOutput;

        var (added, modified, deleted) = ParseNameStatus(nameStatus);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diff)))
            .ToLowerInvariant();

        return new CandidateChangeSet
        {
            TaskId = taskId,
            AgentRunId = agentRunId,
            BaselineCommit = workspace.Baseline.Commit,
            AddedFiles = added,
            ModifiedFiles = modified,
            DeletedFiles = deleted,
            Diff = diff,
            DiffHash = $"sha256:{hash}",
            CreatedAt = DateTime.UtcNow
        };
    }

    public async Task EnsureBaselineUnchangedAsync(
        BaselineSnapshot baseline,
        CancellationToken cancellationToken)
    {
        var currentCommit = (await RunGitAsync(
            baseline.RepositoryPath,
            ["rev-parse", "HEAD"],
            cancellationToken)).StandardOutput.Trim();
        var currentBranch = (await RunGitAsync(
            baseline.RepositoryPath,
            ["rev-parse", "--abbrev-ref", "HEAD"],
            cancellationToken)).StandardOutput.Trim();
        var currentStatus = (await RunGitAsync(
            baseline.RepositoryPath,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken)).StandardOutput;

        if (!string.Equals(currentCommit, baseline.Commit, StringComparison.Ordinal) ||
            !string.Equals(currentBranch, baseline.Branch, StringComparison.Ordinal) ||
            !string.Equals(currentStatus, baseline.GitStatus, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The original repository changed while the candidate was evaluated.");
        }
    }

    internal async Task RemoveWorkspaceAsync(
        StagingWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = ["worktree", "remove", "--force", workspace.Path],
            WorkingDirectory = workspace.Baseline.RepositoryPath,
            Timeout = GitTimeout
        }, cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not remove staging worktree '{workspace.Path}': {FormatFailure(result)}");
        }
    }

    private async Task<ProcessExecutionResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Timeout = GitTimeout
        }, cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Git command failed ({string.Join(' ', arguments)}): {FormatFailure(result)}");
        }

        return result;
    }

    private static (List<string> Added, List<string> Modified, List<string> Deleted)
        ParseNameStatus(string output)
    {
        var added = new List<string>();
        var modified = new List<string>();
        var deleted = new List<string>();
        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < tokens.Length;)
        {
            var status = tokens[index++];
            if (status.Length == 0 || index >= tokens.Length)
                throw new InvalidOperationException("Git returned malformed name-status output.");

            var code = status[0];
            if (code is 'R' or 'C')
            {
                if (index + 1 >= tokens.Length)
                    throw new InvalidOperationException("Git returned an incomplete rename/copy record.");

                var oldPath = NormalizeGitPath(tokens[index++]);
                var newPath = NormalizeGitPath(tokens[index++]);

                if (code == 'R')
                    deleted.Add(oldPath);
                added.Add(newPath);
                continue;
            }

            var path = NormalizeGitPath(tokens[index++]);
            switch (code)
            {
                case 'A':
                    added.Add(path);
                    break;
                case 'D':
                    deleted.Add(path);
                    break;
                default:
                    modified.Add(path);
                    break;
            }
        }

        return (SortDistinct(added), SortDistinct(modified), SortDistinct(deleted));
    }

    private static string NormalizeGitPath(string path) => path.Replace('\\', '/');

    private static List<string> SortDistinct(IEnumerable<string> paths) => paths
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string FormatFailure(ProcessExecutionResult result)
    {
        if (result.TimedOut)
            return "timed out and the process tree was terminated";
        if (result.Cancelled)
            return "cancelled and the process tree was terminated";

        return $"exit code {result.ExitCode}; stderr: {result.StandardError.Trim()}";
    }
}
