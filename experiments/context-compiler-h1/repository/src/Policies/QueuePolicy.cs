using Benchmark.Contracts;

namespace Benchmark.Policies;

public static class QueuePolicy
{
    public static string Name() => QueueContract.PendingQueue;
}
