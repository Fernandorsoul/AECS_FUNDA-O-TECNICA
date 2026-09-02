using AgronomoPlus.Application.Shared.Abstractions;
using AgronomoPlus.Application.Shared.Abstractions.Messaging;
using MediatR;

namespace AgronomoPlus.Application.Modules.Animal.Application.Commands.CreateAnimal;

public record CreateAnimalCommand(
    string Name,
    string Type,
    string Breed,
    DateTime BirthDate,
    decimal Weight,
    string Gender,
    string Tag,
    string Location,
    string HealthStatus)
    : IRequest<Result<Guid>>, ICommand<Result<Guid>>;
