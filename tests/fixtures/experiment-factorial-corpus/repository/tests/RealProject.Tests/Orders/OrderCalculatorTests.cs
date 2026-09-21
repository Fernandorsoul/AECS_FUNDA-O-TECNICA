using FluentAssertions;
using RealProject.Orders;
using Xunit;

namespace RealProject.Tests.Orders;

public class OrderCalculatorTests
{
    private readonly OrderCalculator _calculator = new();

    [Fact]
    public void CalculateDiscount_LessThan5_ReturnsZero()
    {
        var discount = _calculator.CalculateDiscount(3, 100m);

        discount.Should().Be(0);
    }

    [Fact]
    public void CalculateDiscount_5Items_Returns5Percent()
    {
        var discount = _calculator.CalculateDiscount(5, 100m);

        discount.Should().Be(25m);
    }

    [Fact]
    public void CalculateDiscount_10Items_Returns10Percent()
    {
        var discount = _calculator.CalculateDiscount(10, 100m);

        discount.Should().Be(100m);
    }

    [Fact]
    public void CalculateTax_ValidRate_ReturnsCorrectTax()
    {
        var tax = _calculator.CalculateTax(100m, 0.2m);

        tax.Should().Be(20m);
    }

    [Fact]
    public void CalculateTax_NegativeRate_DoesNotReturnNegative()
    {
        var tax = _calculator.CalculateTax(100m, -0.1m);

        tax.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void CalculateTotal_StandardOrder_CalculatesCorrectly()
    {
        var order = new Order { Subtotal = 100m, TaxRate = 0.2m, Quantity = 2 };

        var total = _calculator.CalculateTotal(order);

        total.Should().Be(120m);
    }

    [Fact]
    public void CalculateTotal_WithDiscount_AppliesDiscountBeforeTax()
    {
        var order = new Order { Subtotal = 500m, TaxRate = 0.2m, Quantity = 5 };

        var total = _calculator.CalculateTotal(order);

        total.Should().Be(570m);
    }

    [Fact]
    public void CalculateDiscount_9Items_Returns5Percent()
    {
        var discount = _calculator.CalculateDiscount(9, 100m);

        discount.Should().Be(45m);
    }
}
