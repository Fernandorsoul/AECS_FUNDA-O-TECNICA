using AgronomoPlus.Domain.Models;

namespace AgronomoPlus.Domain.Interfaces;

public interface IPersonRepository : IGenericRepository<Person>
{
    Task<Person?> FindByEmailAsync(string email);
}
