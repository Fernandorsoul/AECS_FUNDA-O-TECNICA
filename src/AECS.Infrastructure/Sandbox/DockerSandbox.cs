using System.Diagnostics;
using System.Text;

namespace AECS.Infrastructure.Sandbox;

public class DockerSandboxOptions
{
    public string Image { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";
    public int TimeoutSeconds { get; set; } = 120;
    public string MemoryLimit { get; set; } = "512m";
    public string CpuLimit { get; set; } = "1.0";
    public bool NetworkAccess { get; set; } = false;
}

public class DockerSandboxResult
{
    public bool Success { get; set; }
    public string StdOut { get; set; } = string.Empty;
    public string StdErr { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ContainerId { get; set; }
}

public class DockerSandbox
{
    private readonly DockerSandboxOptions _options;

    public DockerSandbox(DockerSandboxOptions? options = null)
    {
        _options = options ?? new DockerSandboxOptions();
    }

    public async Task<DockerSandboxResult> ExecuteAsync(
        string workspacePath,
        string command,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string? containerId = null;

        try
        {
            var tempWorkspace = Path.Combine(Path.GetTempPath(), $"aecs-sandbox-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempWorkspace);

            try
            {
                // Copy repo to temp workspace
                CopyDirectory(workspacePath, tempWorkspace);

                // Build docker run command
                var args = BuildRunArgs(tempWorkspace, command);

                var processInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = processInfo };
                var stdOutBuilder = new StringBuilder();
                var stdErrBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null) stdOutBuilder.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null) stdErrBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch { /* best effort */ }
                    throw;
                }

                // Copy modified files back
                CopyDirectory(tempWorkspace, workspacePath, overwrite: true);

                stopwatch.Stop();

                return new DockerSandboxResult
                {
                    Success = process.ExitCode == 0,
                    StdOut = stdOutBuilder.ToString(),
                    StdErr = stdErrBuilder.ToString(),
                    ExitCode = process.ExitCode,
                    Duration = stopwatch.Elapsed,
                    ContainerId = containerId
                };
            }
            finally
            {
                // Cleanup temp workspace
                try { Directory.Delete(tempWorkspace, recursive: true); }
                catch { /* best effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new DockerSandboxResult
            {
                Success = false,
                StdErr = "Execution timed out or was cancelled",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                ContainerId = containerId
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DockerSandboxResult
            {
                Success = false,
                StdErr = $"Docker execution failed: {ex.Message}",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                ContainerId = containerId
            };
        }
    }

    private string BuildRunArgs(string workspacePath, string command)
    {
        var sb = new StringBuilder();

        sb.Append("run --rm");
        sb.Append($" -v \"{workspacePath}:/workspace\"");
        sb.Append(" -w /workspace");
        sb.Append($" --memory={_options.MemoryLimit}");
        sb.Append($" --cpus={_options.CpuLimit}");

        if (!_options.NetworkAccess)
            sb.Append(" --network=none");

        sb.Append($" {_options.Image}");
        sb.Append($" /bin/sh -c \"{command.Replace("\"", "\\\"")}\"");

        return sb.ToString();
    }

    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        var dir = new DirectoryInfo(source);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {source}");

        Directory.CreateDirectory(destination);

        foreach (var file in dir.GetFiles())
        {
            var targetFile = Path.Combine(destination, file.Name);
            file.CopyTo(targetFile, overwrite);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            var targetSubDir = Path.Combine(destination, subDir.Name);
            CopyDirectory(subDir.FullName, targetSubDir, overwrite);
        }
    }
}
