using SampleProject.Customers;
using FluentAssertions;

namespace SampleProject.Tests.Customers;

public class CustomerServiceTests
{
    private readonly CustomerService _service = new();

    [Fact]
    public void Create_ValidInput_AddsCustomer()
    {
        var customer = _service.Create("John", "john@test.com");
        customer.Name.Should().Be("John");
        customer.Email.Should().Be("john@test.com");
        _service.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public void GetById_ExistingId_ReturnsCustomer()
    {
        _service.Create("John", "john@test.com");
        var customer = _service.GetById(1);
        customer.Should().NotBeNull();
        customer!.Name.Should().Be("John");
    }

    [Fact]
    public void GetById_NonExistingId_ReturnsNull()
    {
        var customer = _service.GetById(999);
        customer.Should().BeNull();
    }
}
