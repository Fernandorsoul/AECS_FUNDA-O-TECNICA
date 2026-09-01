namespace Benchmark.Decoys;

public sealed class CacheCatalog
{
    public int Capacity => 128;
    public TimeSpan Lifetime => TimeSpan.FromMinutes(5);
}
