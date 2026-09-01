namespace Benchmark.Decoys;

public sealed class CustomerDirectory
{
    public string Find(string id) => id;
    public IEnumerable<string> List() => [];
}
