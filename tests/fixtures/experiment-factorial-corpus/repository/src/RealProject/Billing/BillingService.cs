namespace RealProject.Billing;

using RealProject.Orders;

public class BillingService
{
    public Invoice CreateInvoice(Order order)
    {
        return new Invoice
        {
            OrderId = order.Id,
            Amount = order.Subtotal,
            IsPaid = false,
            DueDate = DateTime.UtcNow.AddDays(30)
        };
    }

    public bool IsOverdue(Invoice invoice)
    {
        return invoice.DueDate < DateTime.UtcNow;
    }
}

public class Invoice
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public decimal Amount { get; set; }
    public bool IsPaid { get; set; }
    public DateTime DueDate { get; set; }
}
