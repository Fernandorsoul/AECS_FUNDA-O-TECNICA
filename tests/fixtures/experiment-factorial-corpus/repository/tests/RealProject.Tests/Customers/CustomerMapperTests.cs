using FluentAssertions;
using RealProject.Customers;
using Xunit;

namespace RealProject.Tests.Customers;

public class CustomerMapperTests
{
    private readonly CustomerMapper _mapper = new();

    [Fact]
    public void ToDto_ValidCustomer_MapsCorrectly()
    {
        var customer = new Customer { Id = 1, Name = "John", Email = "john@test.com", IsActive = true };

        var dto = _mapper.ToDto(customer);

        dto.Name.Should().Be("JOHN");
        dto.Email.Should().Be("john@test.com");
        dto.IsActive.Should().BeTrue();
    }

    [Fact]
    public void ToDto_NullCustomer_ThrowsArgumentNullException()
    {
        var act = () => _mapper.ToDto(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToEntity_NullDto_ThrowsArgumentNullException()
    {
        var act = () => _mapper.ToEntity(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToEntity_ValidDto_MapsCorrectly()
    {
        var dto = new CustomerDto { Id = 1, Name = "John", Email = "john@test.com", IsActive = true };

        var customer = _mapper.ToEntity(dto);

        customer.Name.Should().Be("John");
        customer.Email.Should().Be("john@test.com");
    }

    [Fact]
    public void RoundTrip_PreservesData()
    {
        var customer = new Customer { Id = 1, Name = "John", Email = "john@test.com", IsActive = true };

        var dto = _mapper.ToDto(customer);
        var result = _mapper.ToEntity(dto);

        result.Id.Should().Be(customer.Id);
        result.Email.Should().Be(customer.Email);
        result.IsActive.Should().Be(customer.IsActive);
    }
}
