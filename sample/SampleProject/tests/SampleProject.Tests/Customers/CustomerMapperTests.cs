using SampleProject.Customers;
using FluentAssertions;

namespace SampleProject.Tests.Customers;

public class CustomerMapperTests
{
    private readonly CustomerMapper _mapper = new();

    [Fact]
    public void MapToDisplay_ValidCustomer_ReturnsFormattedString()
    {
        var customer = new Customer { Name = "John", Email = "john@test.com" };
        var result = _mapper.MapToDisplay(customer);
        result.Should().Be("John (john@test.com)");
    }

    [Fact]
    public void MapToDisplay_NullCustomer_ReturnsEmptyString()
    {
        var result = _mapper.MapToDisplay(null!);
        result.Should().BeEmpty();
    }

    [Fact]
    public void MapPhone_NullCustomer_ReturnsEmptyString()
    {
        var result = _mapper.MapPhone(null!);
        result.Should().BeEmpty();
    }

    [Fact]
    public void MapPhone_WithPhone_ReturnsPhone()
    {
        var customer = new Customer { Phone = "123-456" };
        var result = _mapper.MapPhone(customer);
        result.Should().Be("123-456");
    }

    [Fact]
    public void MapPhone_NullPhone_ReturnsNoPhone()
    {
        var customer = new Customer { Phone = null };
        var result = _mapper.MapPhone(customer);
        result.Should().Be("No phone");
    }
}
