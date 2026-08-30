using SampleProject.Customers;

namespace SampleProject.Customers;

public class CustomerMapper
{
    public Customer MapToCustomer(CustomerDto dto)
    {
        return new Customer
        {
            Id = dto.Id,
            Name = dto.Name,
            Email = dto.Email
        };
    }

    public CustomerDto MapToDto(Customer customer)
    {
        return new CustomerDto
        {
            Id = customer.Id,
            Name = customer.Name,
            Email = customer.Email
        };
    }
}

public class CustomerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
