using SampleProject.Billing;
using FluentAssertions;

namespace SampleProject.Tests.Billing;

public class BillingServiceTests
{
    private readonly BillingService _service = new();

    [Fact]
    public void CalculateTotal_MultipleOrders_ReturnsSum()
    {
        var total = _service.CalculateTotal(customerId: 1, orderTotals: [10m, 20m, 30m]);
        total.Should().Be(60m);
    }

    [Fact]
    public void CalculateTotal_NoOrders_ReturnsZero()
    {
        var total = _service.CalculateTotal(customerId: 1, orderTotals: []);
        total.Should().Be(0m);
    }
}
