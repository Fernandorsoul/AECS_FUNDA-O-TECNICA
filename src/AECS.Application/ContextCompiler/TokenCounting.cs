using AECS.Domain.Models;

namespace AECS.Application.ContextCompiler;

public interface ITokenCounter
{
    string Id { get; }
    string Version { get; }
    bool IsExact { get; }
    bool Supports(string adapter, string model);
    int CountTokens(string value);
}

public interface ITokenCounterResolver
{
    ITokenCounter Resolve(AgentContextProfile profile);
}

public sealed class ConservativeUtf8TokenCounter : ITokenCounter
{
    public string Id => ConservativeTokenCounter.Id;
    public string Version => ConservativeTokenCounter.Version;
    public bool IsExact => false;

    public bool Supports(string adapter, string model) => true;

    public int CountTokens(string value) => ConservativeTokenCounter.Count(value);
}

public sealed class ModelTokenCounterResolver : ITokenCounterResolver
{
    private readonly IReadOnlyList<ITokenCounter> _counters;
    private readonly ITokenCounter _fallback;

    public ModelTokenCounterResolver(IEnumerable<ITokenCounter>? counters = null)
    {
        _fallback = new ConservativeUtf8TokenCounter();
        _counters = (counters ?? [])
            .OrderBy(counter => counter.Id, StringComparer.Ordinal)
            .ThenBy(counter => counter.Version, StringComparer.Ordinal)
            .ToList();
    }

    public ITokenCounter Resolve(AgentContextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _counters.FirstOrDefault(counter =>
                   counter.Id.Equals(profile.TokenizerId, StringComparison.Ordinal) &&
                   counter.Supports(profile.Adapter, profile.Model)) ??
            _fallback;
    }
}
