using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.Classification;

public class RiskClassifier
{
    private static readonly HashSet<string> HighRiskKeywords =
    [
        "auth", "authentication", "authorization", "login", "password", "token",
        "jwt", "oauth", "secret", "credential", "encrypt", "decrypt", "hash",
        "payment", "billing", "charge", "refund", "subscription",
        "migration", "migrate", "database", "schema",
        "infrastructure", "terraform", "docker", "kubernetes", "deploy",
        "permission", "role", "policy", "security"
    ];

    private static readonly HashSet<string> MediumRiskKeywords =
    [
        "service", "repository", "controller", "api", "endpoint", "handler",
        "business", "rule", "validation", "workflow", "process",
        "contract", "interface", "dependency"
    ];

    public RiskLevel Classify(TaskContract contract)
    {
        var text = $"{contract.Objective} {string.Join(" ", contract.AcceptanceCriteria)}".ToLowerInvariant();

        // R0: documentation-only (check first — lowest risk)
        if (text.Contains("comment") || text.Contains("documentation") || text.Contains("readme")
            || text.Contains("format") || text.Contains("rename"))
            return RiskLevel.R0;

        // R3: explicit high-risk constraints
        if (contract.Constraints.SecurityRisk == RiskLevel.R3)
            return RiskLevel.R3;

        // R3: high-risk keywords in objective
        if (HighRiskKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return RiskLevel.R3;

        // R3: database migration or external dependency
        if (contract.Constraints.DatabaseMigration || contract.Constraints.ExternalDependency)
            return RiskLevel.R3;

        // R2: medium-risk keywords or security risk marked as medium
        if (contract.Constraints.SecurityRisk == RiskLevel.R2)
            return RiskLevel.R2;

        if (MediumRiskKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return RiskLevel.R2;

        // R2: large scope (many files allowed)
        if (contract.Scope.Allowed.Count > 3)
            return RiskLevel.R2;

        return RiskLevel.R1;
    }
}
