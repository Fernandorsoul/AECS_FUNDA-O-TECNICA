namespace SampleProject.Customers;

public class CustomerService
{
    private readonly List<Customer> _customers = [];

    public Customer Create(string name, string email)
    {
        // BUG: No validation
        var customer = new Customer
        {
            Id = _customers.Count + 1,
            Name = name,
            Email = email
        };
        _customers.Add(customer);
        return customer;
    }

    public Customer? GetById(int id)
    {
        return _customers.FirstOrDefault(c => c.Id == id);
    }

    public IReadOnlyList<Customer> GetAll()
    {
        return _customers.AsReadOnly();
    }
}
