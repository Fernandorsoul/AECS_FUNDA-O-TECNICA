using FluentAssertions;
using RealProject.Customers;
using Xunit;

namespace RealProject.Tests.Customers;

public class CustomerTests
{
    [Fact]
    public void Equals_SameIdAndEmail_ReturnsTrue()
    {
        var c1 = new Customer { Id = 1, Email = "test@example.com" };
        var c2 = new Customer { Id = 1, Email = "test@example.com" };

        c1.Equals(c2).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        var c1 = new Customer { Id = 1, Email = "test@example.com" };
        var c2 = new Customer { Id = 2, Email = "test@example.com" };

        c1.Equals(c2).Should().BeFalse();
    }

    [Fact]
    public void Equals_Null_DoesNotThrow()
    {
        var c1 = new Customer { Id = 1, Email = "test@example.com" };

        var act = () => c1.Equals(null);

        act.Should().NotThrow();
    }
}
