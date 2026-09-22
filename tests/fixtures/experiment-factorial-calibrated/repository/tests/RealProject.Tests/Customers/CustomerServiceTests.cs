using FluentAssertions;
using RealProject.Customers;
using Xunit;

namespace RealProject.Tests.Customers;

public class CustomerServiceTests
{
    private readonly CustomerService _service = new();

    [Fact]
    public void AddCustomer_ValidCustomer_AddsSuccessfully()
    {
        var customer = new Customer { Id = 1, Name = "John", Email = "john@test.com" };

        _service.AddCustomer(customer);

        _service.GetById(1).Should().NotBeNull();
    }

    [Fact]
    public void GetById_ExistingId_ReturnsCustomer()
    {
        var customer = new Customer { Id = 1, Name = "John", Email = "john@test.com" };
        _service.AddCustomer(customer);

        var result = _service.GetById(1);

        result!.Name.Should().Be("John");
    }

    [Fact]
    public void GetById_NonExistingId_ReturnsNull()
    {
        _service.GetById(999).Should().BeNull();
    }

    [Fact]
    public void GetActive_ReturnsOnlyActiveCustomers()
    {
        _service.AddCustomer(new Customer { Id = 1, Name = "Active", IsActive = true });
        _service.AddCustomer(new Customer { Id = 2, Name = "Inactive", IsActive = false });

        var active = _service.GetActive().ToList();

        active.Should().HaveCount(1);
        active[0].Name.Should().Be("Active");
    }

    [Fact]
    public void Deactivate_NonExistingId_DoesNotThrow()
    {
        var act = () => _service.Deactivate(999);

        act.Should().NotThrow();
    }

    [Fact]
    public void Search_ByEmail_ReturnsMatchingCustomers()
    {
        _service.AddCustomer(new Customer { Id = 1, Name = "John", Email = "john@test.com" });
        _service.AddCustomer(new Customer { Id = 2, Name = "Jane", Email = "jane@test.com" });

        var results = _service.Search("john").ToList();

        results.Should().HaveCount(1);
        results[0].Email.Should().Be("john@test.com");
    }
}
