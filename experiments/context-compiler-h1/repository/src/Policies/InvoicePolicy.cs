using Benchmark.Contracts;

namespace Benchmark.Policies;

public static class InvoicePolicy
{
    public static decimal Rate() => InvoiceContract.PendingRate;
}
