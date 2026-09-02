namespace Benchmark.Decoys;

public sealed class DocumentLedger
{
    public Guid Append(string document) => Guid.Empty;
    public bool Contains(Guid id) => false;
}
