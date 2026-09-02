using Benchmark.Contracts;

namespace Benchmark.Policies;

public static class RetentionPolicy
{
    public static int Days() => RetentionContract.PendingDays;
}
