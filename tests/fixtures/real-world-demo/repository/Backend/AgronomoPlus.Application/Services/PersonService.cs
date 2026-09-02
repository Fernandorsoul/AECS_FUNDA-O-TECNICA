using AgronomoPlus.Application.Interfaces;
using AgronomoPlus.Domain.Interfaces;
using AgronomoPlus.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AgronomoPlus.Application.Services;

public class PersonService : IPersonService
{
    private readonly IPersonRepository _personRepository;
    private readonly IKeycloakAdminService _keycloakAdminService;
    private readonly ILogger<PersonService> _logger;

    public PersonService(
        IPersonRepository personRepository,
        IKeycloakAdminService keycloakAdminService,
        ILogger<PersonService> logger)
    {
        _personRepository = personRepository;
        _keycloakAdminService = keycloakAdminService;
        _logger = logger;
    }

    public async Task<Person> CreateAsync(Person person, string password)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required", nameof(password));

        string keycloakId = string.Empty;
        try
        {
            var roles = string.IsNullOrEmpty(person.Role) ? ["user"] : new[] { person.Role.ToLower() };
            keycloakId = await _keycloakAdminService.CreateUserAsync(
                person.Email,
                person.Name,
                string.Empty,
                roles,
                password);
            person.KeycloakId = keycloakId;
            person.Id = Guid.NewGuid();
            person.CreatedAt = DateTime.UtcNow;
            person.IsActive = true;
            await _personRepository.AddAsync(person);
            return person;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create person; compensating Keycloak user creation.");
            if (!string.IsNullOrEmpty(keycloakId))
                await _keycloakAdminService.DeleteUserAsync(keycloakId);
            throw;
        }
    }

    public Task<IEnumerable<Person>> GetAllAsync() => _personRepository.GetAllAsync();

    public async Task<Person> GetByIdAsync(Guid id) =>
        await _personRepository.GetByIdAsync(id)
        ?? throw new KeyNotFoundException("Person not found.");

    public async Task<Person> UpdateAsync(Guid id, Person person)
    {
        var existingPerson = await GetByIdAsync(id);
        existingPerson.Name = person.Name;
        existingPerson.Email = person.Email;
        existingPerson.Role = person.Role;
        existingPerson.IsActive = person.IsActive;
        await _personRepository.UpdateAsync(existingPerson);
        return existingPerson;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var person = await GetByIdAsync(id);
        if (!string.IsNullOrEmpty(person.KeycloakId))
            await _keycloakAdminService.DeleteUserAsync(person.KeycloakId);
        await _personRepository.DeleteAsync(person);
        return true;
    }
}
