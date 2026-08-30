using System.ComponentModel.DataAnnotations.Schema;

namespace AgronomoPlus.Domain.Models;

[Table("Animais")]
public class Animal
{
    [Column("Id")]
    public Guid Id { get; set; }

    [Column("Nome")]
    public string Name { get; set; } = string.Empty;

    [Column("Tipo")]
    public string Type { get; set; } = string.Empty;

    [Column("Raca")]
    public string Breed { get; set; } = string.Empty;

    [Column("DataNascimento")]
    public DateTime BirthDate { get; set; }

    [Column("Peso")]
    public decimal Weight { get; set; }

    [Column("Genero")]
    public string Gender { get; set; } = string.Empty;

    [Column("Tag")]
    public string Tag { get; set; } = string.Empty;

    [Column("Localizacao")]
    public string Location { get; set; } = string.Empty;

    [Column("StatusSaude")]
    public string HealthStatus { get; set; } = string.Empty;
}
