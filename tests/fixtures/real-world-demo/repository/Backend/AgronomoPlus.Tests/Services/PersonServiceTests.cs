using AgronomoPlus.Application.Services;
using AgronomoPlus.Domain.Interfaces;
using AgronomoPlus.Domain.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AgronomoPlus.Tests.Services;

public class PersonServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldRollbackKeycloak_WhenRepositoryFails()
    {
        var repository = new Mock<IPersonRepository>();
        var keycloak = new Mock<IKeycloakAdminService>();
        var logger = new Mock<ILogger<PersonService>>();
        var person = new Person { Name = "Test", Email = "test@example.com", Role = "User" };
        const string keycloakId = "keycloak-user-id";
        keycloak.Setup(service => service.CreateUserAsync(
                person.Email,
                person.Name,
                string.Empty,
                It.IsAny<string[]>(),
                "SafePassword123"))
            .ReturnsAsync(keycloakId);
        repository.Setup(store => store.AddAsync(It.IsAny<Person>()))
            .ThrowsAsync(new InvalidOperationException("Database failure"));
        var service = new PersonService(repository.Object, keycloak.Object, logger.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(person, "SafePassword123"));

        keycloak.Verify(service => service.DeleteUserAsync(keycloakId), Times.Once);
    }
}
