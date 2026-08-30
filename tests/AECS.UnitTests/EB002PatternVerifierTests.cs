using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public class EB002PatternVerifierTests
{
    [Fact]
    public void Verify_ServiceWithoutInterface_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var appDir = Path.Combine(tempDir, "AECS.Application", "Services");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "CustomerService.cs"), """
                namespace AECS.Application.Services;

                public class CustomerService
                {
                    public void Create() { }
                }
                """);

            var verifier = new EB002PatternVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB002-SERVICE-INTERFACE");
            result.Violations.Should().Contain(v => v.ExpectedPattern == "ICustomerService");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_ServiceWithInterface_NoViolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var appDir = Path.Combine(tempDir, "AECS.Application", "Services");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "ICustomerService.cs"), """
                namespace AECS.Application.Services;

                public interface ICustomerService
                {
                    void Create();
                }
                """);
            File.WriteAllText(Path.Combine(appDir, "CustomerService.cs"), """
                namespace AECS.Application.Services;

                public class CustomerService : ICustomerService
                {
                    public void Create() { }
                }
                """);

            var verifier = new EB002PatternVerifier();
            var result = verifier.Verify(tempDir);

            result.Violations.Should().NotContain(v => v.RuleId == "EB002-SERVICE-INTERFACE");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_RepoWithoutInterface_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var infraDir = Path.Combine(tempDir, "AECS.Infrastructure", "Repositories");
            Directory.CreateDirectory(infraDir);
            File.WriteAllText(Path.Combine(infraDir, "CustomerRepository.cs"), """
                namespace AECS.Infrastructure.Repositories;

                public class CustomerRepository
                {
                    public void GetAll() { }
                }
                """);

            var verifier = new EB002PatternVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB002-REPO-INTERFACE");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_AsyncMethodWithoutSuffix_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var appDir = Path.Combine(tempDir, "AECS.Application", "Services");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "MyService.cs"), """
                using System.Threading.Tasks;

                namespace AECS.Application.Services;

                public class MyService
                {
                    public async Task DoWork()
                    {
                        await Task.Delay(1);
                    }
                }
                """);

            var verifier = new EB002PatternVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB002-ASYNC-NAMING");
            result.Violations.Should().Contain(v => v.ActualPattern == "DoWork");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_AsyncMethodWithSuffix_NoViolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var appDir = Path.Combine(tempDir, "AECS.Application", "Services");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "MyService.cs"), """
                using System.Threading.Tasks;

                namespace AECS.Application.Services;

                public class MyService
                {
                    public async Task DoWorkAsync()
                    {
                        await Task.Delay(1);
                    }
                }
                """);

            var verifier = new EB002PatternVerifier();
            var result = verifier.Verify(tempDir);

            result.Violations.Should().NotContain(v => v.RuleId == "EB002-ASYNC-NAMING");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_ControllerDirectInstantiation_Violation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var apiDir = Path.Combine(tempDir, "AECS.Api", "Controllers");
            Directory.CreateDirectory(apiDir);
            File.WriteAllText(Path.Combine(apiDir, "CustomerController.cs"), """
                namespace AECS.Api.Controllers;

                public class CustomerController
                {
                    public void Create()
                    {
                        var service = new CustomerService();
                    }
                }
                """);

            var verifier = new EB002PatternVerifier();
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "EB002-CONTROLLER-DI");
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
            var appDir = Path.Combine(tempDir, "MyApp", "Handlers");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "CreateOrderHandler.cs"), """
                namespace MyApp.Handlers;

                public class CreateOrderHandler
                {
                    public void Handle() { }
                }
                """);

            var customRules = new List<PatternRule>
            {
                new()
                {
                    Id = "CUSTOM-HANDLER",
                    Name = "Handlers must implement interface",
                    Type = PatternType.InterfaceImplementation,
                    Pattern = @"class\s+(\w+Handler)\b",
                    ExpectedPattern = @"interface\s+I\1",
                    Scope = "Handlers",
                    Severity = RuleSeverity.Warning
                }
            };

            var verifier = new EB002PatternVerifier(customRules);
            var result = verifier.Verify(tempDir);

            result.HasViolations.Should().BeTrue();
            result.Violations.Should().Contain(v => v.RuleId == "CUSTOM-HANDLER");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
