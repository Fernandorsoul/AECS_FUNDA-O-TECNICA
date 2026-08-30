using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public class EB001ArchitectureVerifierTests
{
    [Fact]
    public void Verify_CleanArchitecture_NoViolations()
    {
        // Create a temp directory with clean architecture
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Domain file — no forbidden dependencies
            var domainDir = Path.Combine(tempDir, "AECS.Domain", "Models");
            Directory.CreateDirectory(domainDir);
            File.WriteAllText(Path.Combine(domainDir, "Customer.cs"), """
                namespace AECS.Domain.Models;

                public class Customer
                {
                    public int Id { get; set; }
                    public string Name { get; set; } = string.Empty;
                }
                """);

            var verifier = new EB001ArchitectureVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeFalse();
            result.FilesScanned.Should().Be(1);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_DomainUsingEFCore_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var domainDir = Path.Combine(tempDir, "AECS.Domain", "Models");
            Directory.CreateDirectory(domainDir);
            File.WriteAllText(Path.Combine(domainDir, "Customer.cs"), """
                using Microsoft.EntityFrameworkCore;

                namespace AECS.Domain.Models;

                public class Customer
                {
                    public int Id { get; set; }
                }
                """);

            var verifier = new EB001ArchitectureVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB001-DOMAIN-ORM");
            result.Violations.Should().Contain(v => v.ForbiddenDependency.Contains("EntityFrameworkCore"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_DomainUsingHttpClient_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var domainDir = Path.Combine(tempDir, "AECS.Domain", "Services");
            Directory.CreateDirectory(domainDir);
            File.WriteAllText(Path.Combine(domainDir, "ExternalService.cs"), """
                using System.Net.Http;

                namespace AECS.Domain.Services;

                public class ExternalService
                {
                    private readonly HttpClient _client;
                }
                """);

            var verifier = new EB001ArchitectureVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB001-DOMAIN-NETWORK");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_DomainUsingApplication_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var domainDir = Path.Combine(tempDir, "AECS.Domain", "Models");
            Directory.CreateDirectory(domainDir);
            File.WriteAllText(Path.Combine(domainDir, "Customer.cs"), """
                using AECS.Application.Services;

                namespace AECS.Domain.Models;

                public class Customer
                {
                    public int Id { get; set; }
                }
                """);

            var verifier = new EB001ArchitectureVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB001-DOMAIN");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_ApplicationUsingInfrastructure_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var appDir = Path.Combine(tempDir, "AECS.Application", "Services");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "MyService.cs"), """
                using AECS.Infrastructure.Persistence;

                namespace AECS.Application.Services;

                public class MyService
                {
                }
                """);

            var verifier = new EB001ArchitectureVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB001-APP");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_InfrastructureUsingApplication_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var infraDir = Path.Combine(tempDir, "AECS.Infrastructure", "Repositories");
            Directory.CreateDirectory(infraDir);
            File.WriteAllText(Path.Combine(infraDir, "MyRepo.cs"), """
                using AECS.Application.Services;

                namespace AECS.Infrastructure.Repositories;

                public class MyRepo
                {
                }
                """);

            var verifier = new EB001ArchitectureVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB001-INFRA");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_MultipleViolations_AllDetected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Domain using EF Core
            var domainDir = Path.Combine(tempDir, "AECS.Domain", "Models");
            Directory.CreateDirectory(domainDir);
            File.WriteAllText(Path.Combine(domainDir, "Customer.cs"), """
                using Microsoft.EntityFrameworkCore;
                using System.Net.Http;

                namespace AECS.Domain.Models;

                public class Customer
                {
                    public int Id { get; set; }
                }
                """);

            var verifier = new EB001ArchitectureVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().HaveCount(2);
            result.Violations.Should().Contain(v => v.RuleId == "EB001-DOMAIN-ORM");
            result.Violations.Should().Contain(v => v.RuleId == "EB001-DOMAIN-NETWORK");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_CustomRules_Applied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var appDir = Path.Combine(tempDir, "MyApp", "Controllers");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "HomeController.cs"), """
                using MyApp.Data;

                namespace MyApp.Controllers;

                public class HomeController
                {
                }
                """);

            var customRules = new List<ArchitectureRule>
            {
                new()
                {
                    Id = "CUSTOM-001",
                    Name = "Controllers cannot use Data layer",
                    SourceLayer = "MyApp.Controllers",
                    ForbiddenDependencies = ["MyApp.Data"],
                    Severity = RuleSeverity.Error
                }
            };

            var verifier = new EB001ArchitectureVerifier(customRules);
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "CUSTOM-001");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
