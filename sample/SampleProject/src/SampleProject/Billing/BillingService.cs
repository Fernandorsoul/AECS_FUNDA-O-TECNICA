namespace SampleProject.Billing;

public class BillingService
{
    public decimal CalculateTotal(int customerId, IEnumerable<decimal> orderTotals)
    {
        return orderTotals.Sum();
    }
}
