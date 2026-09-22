namespace RealProject.Orders;

public class OrderCalculator
{
    public decimal CalculateDiscount(int quantity, decimal unitPrice)
    {
        if (quantity >= 10)
            return quantity * unitPrice * 0.10m;
        if (quantity >= 5)
            return quantity * unitPrice * 0.05m;
        return 0;
    }

    public decimal CalculateTax(decimal subtotal, decimal taxRate)
    {
        if (taxRate < 0)
            return 0;
        return subtotal * taxRate;
    }

    public decimal CalculateTotal(Order order)
    {
        var discount = CalculateDiscount(order.Quantity, order.Subtotal / order.Quantity);
        var tax = CalculateTax(order.Subtotal - discount, order.TaxRate);
        return order.Subtotal - discount + tax;
    }
}
