namespace Benchmark.Decoys;

public sealed class AddressBook
{
    public IReadOnlyList<string> Entries { get; } = [];
    public string Normalize(string value) => value.Trim().ToUpperInvariant();
}
