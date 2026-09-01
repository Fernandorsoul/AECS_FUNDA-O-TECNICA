using System.Diagnostics;
using Benchmark.Contracts;
using Benchmark.Policies;

var trackedChanges = Git("diff", "--name-only", "HEAD");
var untrackedChanges = Git("ls-files", "--others", "--exclude-standard");
var changed = (trackedChanges + "\n" + untrackedChanges)
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(path => path.Replace('\\', '/'))
    .Where(path => path.StartsWith("src/", StringComparison.Ordinal))
    .Distinct(StringComparer.Ordinal)
    .ToList();
if (changed.Count == 0)
    return 0;
if (changed.Count != 1)
    return Fail("A benchmark candidate must change exactly one source policy.");

var valid = changed[0] switch
{
    "src/Policies/RetentionPolicy.cs" =>
        RetentionPolicy.Days() == RetentionContract.ProductionDays,
    "src/Policies/InvoicePolicy.cs" =>
        InvoicePolicy.Rate() == InvoiceContract.ProductionRate,
    "src/Policies/QueuePolicy.cs" =>
        QueuePolicy.Name() == QueueContract.ProductionQueue,
    _ => false
};
return valid ? 0 : Fail($"The changed policy does not implement its production contract: {changed[0]}");

static string Git(params string[] arguments)
{
    using var process = Process.Start(new ProcessStartInfo("git", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    }) ?? throw new InvalidOperationException("Unable to start git.");
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException(process.StandardError.ReadToEnd());
    return output;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
