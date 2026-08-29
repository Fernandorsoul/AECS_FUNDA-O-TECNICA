namespace SampleProject.Customers;

/// <summary>
/// Maps Customer entities to display strings.
/// BUG: This class throws NullReferenceException when customer is null.
/// </summary>
public class CustomerMapper
{
    public string MapToDisplay(Customer customer)
    {
        // BUG: No null check — will throw NullReferenceException
        return $"{customer.Name} ({customer.Email})";
    }

    public string MapPhone(Customer customer)
    {
        // BUG: No null check — will throw NullReferenceException
        return customer.Phone ?? "No phone";
    }

    public CustomerSummary MapToSummary(Customer customer)
    {
        // BUG: No null check — will throw NullReferenceException
        return new CustomerSummary
        {
            DisplayName = customer.Name,
            HasPhone = customer.Phone != null
        };
    }
}

public class CustomerSummary
{
    public string DisplayName { get; set; } = string.Empty;
    public bool HasPhone { get; set; }
}
