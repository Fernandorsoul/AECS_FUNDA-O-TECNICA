using AgronomoPlus.Application.Shared.Abstractions;
using AgronomoPlus.Application.Shared.Abstractions.Messaging;
using AgronomoPlus.Domain.Interfaces;

namespace AgronomoPlus.Application.Modules.Animal.Application.Commands.CreateAnimal;

public class CreateAnimalHandler : ICommandHandler<CreateAnimalCommand, Result<Guid>>
{
    private readonly IAnimalRepository _animalRepository;

    public CreateAnimalHandler(IAnimalRepository animalRepository)
    {
        _animalRepository = animalRepository ?? throw new ArgumentNullException(nameof(animalRepository));
    }

    public async Task<Result<Guid>> Handle(
        CreateAnimalCommand request,
        CancellationToken cancellationToken)
    {
        var animal = new Domain.Models.Animal
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Type = request.Type.Trim(),
            Breed = request.Breed.Trim(),
            BirthDate = request.BirthDate,
            Weight = request.Weight,
            Gender = request.Gender.Trim(),
            Tag = request.Tag.Trim(),
            Location = request.Location.Trim(),
            HealthStatus = request.HealthStatus.Trim()
        };

        await _animalRepository.AddAsync(animal);
        return Result<Guid>.Success(animal.Id);
    }
}
