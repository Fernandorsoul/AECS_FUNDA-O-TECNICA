using SampleProject.Customers;

namespace SampleProject.Tests;

public class CustomerMapperTests
{
    private readonly CustomerMapper _mapper = new();

    [Fact]
    public void MapToCustomer_ValidDto_ReturnsCustomer()
    {
        var dto = new CustomerDto { Id = 1, Name = "John", Email = "john@example.com" };

        var customer = _mapper.MapToCustomer(dto);

        Assert.Equal(1, customer.Id);
        Assert.Equal("John", customer.Name);
        Assert.Equal("john@example.com", customer.Email);
    }

    [Fact]
    public void MapToDto_ValidCustomer_ReturnsDto()
    {
        var customer = new Customer { Id = 2, Name = "Jane", Email = "jane@example.com" };

        var dto = _mapper.MapToDto(customer);

        Assert.Equal(2, dto.Id);
        Assert.Equal("Jane", dto.Name);
        Assert.Equal("jane@example.com", dto.Email);
    }
}
