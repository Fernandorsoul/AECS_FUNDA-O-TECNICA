using System.Security.Cryptography;
using System.Text;
using AECS.Application.ControlKernel;
using AECS.Application.Staging;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public sealed class AdaptiveOfflineTaskBaseline
{
    public string RepositoryId { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string SourceUri { get; init; } = string.Empty;
    public string LicenseSpdx { get; init; } = string.Empty;
    public string UpstreamCommit { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string OracleDiffHash { get; init; } = string.Empty;
    public string ContainerImage { get; init; } = string.Empty;
}

public sealed class AdaptiveOfflineTaskBaselineVerifier
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _processRunner;
    private readonly GitWorkspaceManager _workspaceManager;

    public AdaptiveOfflineTaskBaselineVerifier(IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
        _workspaceManager = new GitWorkspaceManager(processRunner);
    }

    public async Task<AdaptiveOfflineTaskBaseline> VerifyAsync(
        LoadedAdaptiveOfflineDataset dataset,
        AdaptiveOfflineTaskDefinition task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(task);
        if (!dataset.IsMultiBaseline)
        {
            throw new InvalidOperationException(
                "Per-task baseline verification requires adaptive offline dataset v2.");
        }
        AdaptiveOfflineDatasetContract.Validate(dataset.Manifest);
        var repository = dataset.Manifest.Repositories!.Single(entry =>
            entry.Id.Equals(task.RepositoryId, StringComparison.Ordinal));
        var repositoryPath = dataset.RepositoryPathFor(task);
        var captured = await _workspaceManager.CaptureBaselineAsync(
            repositoryPath,
            cancellationToken);
        if (!captured.RepositoryPath.Equals(
                Path.GetFullPath(repositoryPath),
                StringComparison.OrdinalIgnoreCase) ||
            !captured.Commit.Equals(task.BaselineCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Repository '{repository.Id}' path or HEAD does not match the task baseline.");
        }

        var remote = (await GitAsync(
            repositoryPath,
            ["remote", "get-url", "origin"],
            cancellationToken)).StandardOutput.Trim();
        if (!CanonicalUri(remote).Equals(
                CanonicalUri(repository.SourceUri),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Repository '{repository.Id}' origin does not match its preregistered source.");
        }

        var parents = (await GitAsync(
            repositoryPath,
            ["rev-list", "--parents", "-n", "1", task.BaselineCommit!],
            cancellationToken)).StandardOutput.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
        if (parents.Length != 2 ||
            !parents[0].Equals(task.BaselineCommit, StringComparison.OrdinalIgnoreCase) ||
            !parents[1].Equals(task.UpstreamCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Task '{task.Id}' baseline must be a single oracle commit directly above upstream.");
        }

        var diff = (await GitAsync(
            repositoryPath,
            ["diff", "--binary", "--no-ext-diff", task.UpstreamCommit!, task.BaselineCommit!, "--"],
            cancellationToken)).StandardOutput;
        var observedDiffHash = HashDiff(diff);
        if (!observedDiffHash.Equals(task.Oracle!.DiffHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Task '{task.Id}' oracle diff hash does not match the preregistered value.");
        }

        var changedFiles = (await GitAsync(
            repositoryPath,
            ["diff", "--name-only", "-z", task.UpstreamCommit!, task.BaselineCommit!, "--"],
            cancellationToken)).StandardOutput.Split(
                '\0',
                StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .ToList();
        if (changedFiles.Count == 0 || changedFiles.Any(path =>
                !task.Oracle.AllowedPaths.Any(pattern =>
                    ScopeEnforcer.MatchesGlob(path, pattern))))
        {
            throw new InvalidOperationException(
                $"Task '{task.Id}' oracle changes files outside its declared test-only scope.");
        }

        return new AdaptiveOfflineTaskBaseline
        {
            RepositoryId = repository.Id,
            RepositoryPath = repositoryPath,
            SourceUri = repository.SourceUri,
            LicenseSpdx = repository.LicenseSpdx,
            UpstreamCommit = task.UpstreamCommit!,
            BaselineCommit = task.BaselineCommit!,
            OracleDiffHash = observedDiffHash,
            ContainerImage = task.Oracle.ContainerImage
        };
    }

    public static string HashDiff(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(diff))).ToLowerInvariant();
    }

    private async Task<ProcessExecutionResult> GitAsync(
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
                $"Git provenance check failed ({string.Join(' ', arguments)}): " +
                (result.TimedOut ? "timed out" : result.StandardError.Trim()));
        }
        return result;
    }

    private static string CanonicalUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Adaptive offline repository origin must be an absolute HTTP(S) URI.");
        }
        var builder = new UriBuilder(uri)
        {
            Host = uri.Host.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };
        var canonical = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return canonical.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? canonical[..^4]
            : canonical;
    }
}
