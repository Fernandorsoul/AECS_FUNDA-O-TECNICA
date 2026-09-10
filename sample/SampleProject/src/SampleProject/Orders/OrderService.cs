namespace SampleProject.Orders;

public class OrderService
{
    private readonly List<Order> _orders = [];

    public Order Create(int customerId, decimal total)
    {
        var order = new Order
        {
            Id = _orders.Count + 1,
            CustomerId = customerId,
            Total = total
        };
        _orders.Add(order);
        return order;
    }

    public IReadOnlyList<Order> GetByCustomer(int customerId)
    {
        return _orders.Where(o => o.CustomerId == customerId).ToList().AsReadOnly();
    }
}
