namespace RealProject.Orders;

public class Order
{
    public int Id { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxRate { get; set; }
    public int Quantity { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
