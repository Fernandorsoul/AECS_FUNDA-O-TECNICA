namespace RealProject.Customers;

public class CustomerService
{
    private readonly List<Customer> _customers = new();

    public void AddCustomer(Customer customer)
    {
        _customers.Add(customer);
    }

    public Customer? GetById(int id)
    {
        return _customers.FirstOrDefault(c => c.Id == id);
    }

    public IEnumerable<Customer> GetActive()
    {
        return _customers.Where(c => c.IsActive);
    }

    public void Deactivate(int id)
    {
        var customer = GetById(id);
        if (customer is null)
            return;
        customer.IsActive = false;
    }

    public IEnumerable<Customer> Search(string term)
    {
        return _customers.Where(c => c.Name.Contains(term) || c.Email.Contains(term));
    }
}
