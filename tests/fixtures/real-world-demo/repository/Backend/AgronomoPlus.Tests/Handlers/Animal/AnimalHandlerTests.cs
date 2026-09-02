using AgronomoPlus.Application.Modules.Animal.Application.Commands.CreateAnimal;
using AgronomoPlus.Domain.Interfaces;
using Moq;
using Xunit;

namespace AgronomoPlus.Tests.Handlers.Animal;

public class CreateAnimalHandlerTests
{
    private readonly Mock<IAnimalRepository> _mockRepo = new();
    private readonly CreateAnimalHandler _handler;

    public CreateAnimalHandlerTests()
    {
        _handler = new CreateAnimalHandler(_mockRepo.Object);
    }

    [Fact]
    public async Task Handle_ShouldReturnSuccess_WhenDataIsValid()
    {
        var command = new CreateAnimalCommand(
            "Nelore",
            "Bovino",
            "Nelore Puro",
            DateTime.UtcNow.AddYears(-2),
            450m,
            "male",
            "TAG-001",
            "Pasto A",
            "Healthy");
        _mockRepo.Setup(repository => repository.AddAsync(It.IsAny<Domain.Models.Animal>()))
            .Returns(Task.CompletedTask);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value);
        _mockRepo.Verify(
            repository => repository.AddAsync(It.IsAny<Domain.Models.Animal>()),
            Times.Once);
    }
}
