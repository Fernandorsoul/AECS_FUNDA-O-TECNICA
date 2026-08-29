using AECS.Application.Classification;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class RiskClassifierTests
{
    private readonly RiskClassifier _classifier = new();

    [Fact]
    public void Classify_NullHandling_ReturnsR1()
    {
        var contract = new TaskContract
        {
            Id = "T1",
            Objective = "Fix null handling in CustomerMapper",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Customers/**", "tests/Customers/**"]
            }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R1);
    }

    [Fact]
    public void Classify_AuthRelated_ReturnsR3()
    {
        var contract = new TaskContract
        {
            Id = "T2",
            Objective = "Implement password recovery with JWT tokens",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Auth/**"]
            }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R3);
    }

    [Fact]
    public void Classify_PaymentRelated_ReturnsR3()
    {
        var contract = new TaskContract
        {
            Id = "T3",
            Objective = "Add Stripe payment processing for subscriptions",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Billing/**"]
            }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R3);
    }

    [Fact]
    public void Classify_DatabaseMigration_ReturnsR3()
    {
        var contract = new TaskContract
        {
            Id = "T4",
            Objective = "Add new column to customers table",
            Constraints = new TaskConstraints { DatabaseMigration = true }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R3);
    }

    [Fact]
    public void Classify_BusinessLogic_ReturnsR2()
    {
        var contract = new TaskContract
        {
            Id = "T5",
            Objective = "Add validation service for order processing",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Orders/**", "tests/Orders/**"]
            }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R2);
    }

    [Fact]
    public void Classify_Documentation_ReturnsR0()
    {
        var contract = new TaskContract
        {
            Id = "T6",
            Objective = "Add comments and documentation to CustomerMapper",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Customers/**"]
            }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R0);
    }

    [Fact]
    public void Classify_LargeScope_ReturnsR2()
    {
        var contract = new TaskContract
        {
            Id = "T7",
            Objective = "Update DTOs across modules",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Customers/**", "src/Orders/**", "src/Billing/**", "src/Shared/**"]
            }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R2);
    }

    [Fact]
    public void Classify_ExplicitHighRisk_ReturnsR3()
    {
        var contract = new TaskContract
        {
            Id = "T8",
            Objective = "Simple change",
            Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R3 }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R3);
    }

    [Fact]
    public void Classify_ExternalDependency_ReturnsR3()
    {
        var contract = new TaskContract
        {
            Id = "T9",
            Objective = "Add new NuGet package for PDF generation",
            Constraints = new TaskConstraints { ExternalDependency = true }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R3);
    }
}
