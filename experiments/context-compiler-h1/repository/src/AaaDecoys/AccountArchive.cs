namespace Benchmark.Decoys;

public sealed class AccountArchive
{
    public string Store(string value) => value.Trim();
    public bool IsAvailable => true;
}
