using System.Globalization;
using AECS.Domain.Models;

namespace AECS.Application.ControlKernel;

public static class CapabilityPolicyGuard
{
    public static void EnsureNoExpansion(
        ExecutionCapabilityPolicy authoritative,
        ExecutionCapabilityPolicy proposed)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        ArgumentNullException.ThrowIfNull(proposed);
        if (authoritative.Version != proposed.Version ||
            authoritative.Authority != proposed.Authority ||
            !IsSubset(proposed.FileSystem.Read, authoritative.FileSystem.Read) ||
            !IsSubset(proposed.FileSystem.Write, authoritative.FileSystem.Write) ||
            !IsSubset(ProcessGrants(proposed), ProcessGrants(authoritative)) ||
            !IsSubset(proposed.Network.Destinations, authoritative.Network.Destinations) ||
            !IsSubset(proposed.Network.Phases, authoritative.Network.Phases) ||
            !IsSubset(SecretGrants(proposed), SecretGrants(authoritative)) ||
            ParseCpu(proposed.Resources.CpuLimit) > ParseCpu(authoritative.Resources.CpuLimit) ||
            ParseMemory(proposed.Resources.MemoryLimit) > ParseMemory(authoritative.Resources.MemoryLimit) ||
            proposed.Resources.ProcessLimit > authoritative.Resources.ProcessLimit ||
            proposed.Resources.WallClockSeconds > authoritative.Resources.WallClockSeconds)
        {
            throw new InvalidOperationException(
                "Adaptive execution policy attempted to expand authoritative task capabilities.");
        }
    }

    private static IEnumerable<string> ProcessGrants(ExecutionCapabilityPolicy policy) =>
        policy.Processes.SelectMany(rule => rule.Phases.Select(phase => string.Join('|',
            rule.Executable,
            string.Join('\u001f', rule.ArgumentPrefix),
            phase)));

    private static IEnumerable<string> SecretGrants(ExecutionCapabilityPolicy policy) =>
        policy.Secrets.SelectMany(secret => secret.Phases.Select(phase =>
            string.Join('|', secret.Name, phase)));

    private static bool IsSubset(IEnumerable<string> proposed, IEnumerable<string> authoritative)
    {
        var allowed = authoritative.ToHashSet(StringComparer.Ordinal);
        return proposed.All(allowed.Contains);
    }

    private static decimal ParseCpu(string value) => decimal.Parse(
        value,
        NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture);

    private static long ParseMemory(string value)
    {
        var suffix = char.ToLowerInvariant(value[^1]);
        var hasSuffix = suffix is 'k' or 'm' or 'g';
        var amount = long.Parse(hasSuffix ? value[..^1] : value, CultureInfo.InvariantCulture);
        return checked(amount * (suffix switch
        {
            'k' => 1024L,
            'm' => 1024L * 1024,
            'g' => 1024L * 1024 * 1024,
            _ => 1L
        }));
    }
}
