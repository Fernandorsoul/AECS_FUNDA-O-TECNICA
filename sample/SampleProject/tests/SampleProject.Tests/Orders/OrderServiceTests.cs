using SampleProject.Orders;
using FluentAssertions;

namespace SampleProject.Tests.Orders;

public class OrderServiceTests
{
    private readonly OrderService _service = new();

    [Fact]
    public void Create_ValidInput_ReturnsOrder()
    {
        var order = _service.Create(customerId: 1, total: 99.99m);
        order.Should().NotBeNull();
        order.CustomerId.Should().Be(1);
        order.Total.Should().Be(99.99m);
    }

    [Fact]
    public void GetByCustomer_ExistingCustomer_ReturnsOrders()
    {
        _service.Create(customerId: 1, total: 10m);
        _service.Create(customerId: 1, total: 20m);
        _service.Create(customerId: 2, total: 30m);

        var orders = _service.GetByCustomer(1);
        orders.Should().HaveCount(2);
    }

    [Fact]
    public void GetByCustomer_NoOrders_ReturnsEmpty()
    {
        var orders = _service.GetByCustomer(999);
        orders.Should().BeEmpty();
    }
}
