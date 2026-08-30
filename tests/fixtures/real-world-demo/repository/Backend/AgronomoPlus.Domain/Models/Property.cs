namespace AgronomoPlus.Domain.Models;

public class Property
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public decimal AreaSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public Person Person { get; set; } = null!;
}
