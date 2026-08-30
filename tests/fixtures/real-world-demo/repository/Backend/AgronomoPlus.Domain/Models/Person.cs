using System.ComponentModel.DataAnnotations.Schema;

namespace AgronomoPlus.Domain.Models;

[Table("Produtores")]
public class Person
{
    [Column("Id")]
    public Guid Id { get; set; }

    [Column("SubjectId")]
    public string? KeycloakId { get; set; }

    [Column("Nome")]
    public string Name { get; set; } = string.Empty;

    [Column("Email")]
    public string Email { get; set; } = string.Empty;

    [Column("Role")]
    public string Role { get; set; } = "Role_User";

    [Column("DataCriacao")]
    public DateTime CreatedAt { get; set; }

    [Column("UltimoAcesso")]
    public DateTime? LastAccess { get; set; }

    [Column("Ativo")]
    public bool IsActive { get; set; }

    public ICollection<Property> Properties { get; set; } = new List<Property>();
}
