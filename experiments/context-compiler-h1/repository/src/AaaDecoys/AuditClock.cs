namespace Benchmark.Decoys;

public sealed class AuditClock
{
    public DateTimeOffset Now() => DateTimeOffset.UnixEpoch;
    public string Zone => "UTC";
}
