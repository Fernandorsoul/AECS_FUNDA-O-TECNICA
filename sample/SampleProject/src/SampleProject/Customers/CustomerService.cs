using SampleProject.Customers;

namespace SampleProject.Customers;

public class CustomerService
{
    private readonly List<Customer> _customers = new();

    public Customer GetCustomerById(int id)
    {
        return _customers.FirstOrDefault(c => c.Id == id) 
            ?? throw new KeyNotFoundException($"Customer with ID {id} not found");
    }

    public void AddCustomer(Customer customer)
    {
        if (customer == null)
            throw new ArgumentNullException(nameof(customer));
        
        _customers.Add(customer);
    }

    public IEnumerable<Customer> GetAllCustomers()
    {
        return _customers.AsReadOnly();
    }
}
