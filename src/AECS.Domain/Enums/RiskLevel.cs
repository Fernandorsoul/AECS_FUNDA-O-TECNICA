namespace AECS.Domain.Enums;

public enum RiskLevel
{
    R0, // documentation, comments, formatting
    R1, // UI, DTO, simple CRUD
    R2, // business rules, data access, API contracts
    R3  // auth, payments, migrations, infra, crypto, secrets
}
