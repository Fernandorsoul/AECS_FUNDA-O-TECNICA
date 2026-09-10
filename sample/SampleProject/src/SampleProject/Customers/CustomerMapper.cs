namespace SampleProject.Customers;

public class CustomerMapper
{
    public string MapToDisplay(Customer customer)
    {
        // BUG: No null check
        return $"{customer.Name} ({customer.Email})";
    }

    public string MapPhone(Customer customer)
    {
        // BUG: No null check
        return customer.Phone ?? "No phone";
    }
}
