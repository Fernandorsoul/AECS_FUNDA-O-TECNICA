using AECS.Application.ContextCompiler;
using AECS.Domain.Models;

namespace AECS.Application.Experiments;

public sealed class RequiredContextPathsGate : ICompiledContextGate
{
    private readonly IReadOnlyList<string> _requiredPaths;

    public RequiredContextPathsGate(IEnumerable<string> requiredPaths)
    {
        ArgumentNullException.ThrowIfNull(requiredPaths);
        _requiredPaths = requiredPaths
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    public void EnsureAccepted(TaskContract contract, CompiledRepositoryContext context)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(context);

        var included = context.Manifest.Files
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        var missing = _requiredPaths.Where(path => !included.Contains(path)).ToList();
        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Context preflight rejected task '{contract.Id}' before provider execution; " +
            $"missing required path(s): {string.Join(", ", missing)}.");
    }
}
