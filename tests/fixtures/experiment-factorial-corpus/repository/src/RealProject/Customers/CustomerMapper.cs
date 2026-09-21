namespace RealProject.Customers;

public class CustomerMapper
{
    public CustomerDto ToDto(Customer customer)
    {
        return new CustomerDto
        {
            Id = customer.Id,
            Name = customer.Name.ToUpper(),
            Email = customer.Email,
            IsActive = customer.IsActive
        };
    }

    public Customer ToEntity(CustomerDto dto)
    {
        return new Customer
        {
            Id = dto.Id,
            Name = dto.Name,
            Email = dto.Email,
            IsActive = dto.IsActive
        };
    }
}

public class CustomerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
