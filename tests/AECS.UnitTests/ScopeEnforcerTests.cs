using AECS.Application.ControlKernel;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class ScopeEnforcerTests
{
    private readonly ScopeEnforcer _enforcer = new();

    [Fact]
    public void Check_AllFilesAllowed_ReturnsEmpty()
    {
        var scope = new ScopeDefinition
        {
            Allowed = ["src/Customers/**", "tests/Customers/**"]
        };
        var files = new List<string>
        {
            "src/Customers/CustomerMapper.cs",
            "tests/Customers/CustomerMapperTests.cs"
        };

        var violations = _enforcer.Check(scope, files);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Check_ForbiddenFile_ReturnsViolation()
    {
        var scope = new ScopeDefinition
        {
            Allowed = ["src/Customers/**"],
            Forbidden = ["src/Billing/**"]
        };
        var files = new List<string>
        {
            "src/Customers/CustomerMapper.cs",
            "src/Billing/BillingService.cs"
        };

        var violations = _enforcer.Check(scope, files);

        violations.Should().HaveCount(1);
        violations[0].ViolationType.Should().Be("ForbiddenFile");
        violations[0].FilePath.Should().Be("src/Billing/BillingService.cs");
    }

    [Fact]
    public void Check_FileNotInAllowed_ReturnsViolation()
    {
        var scope = new ScopeDefinition
        {
            Allowed = ["src/Customers/**"]
        };
        var files = new List<string>
        {
            "src/Customers/CustomerMapper.cs",
            "src/Orders/OrderService.cs"
        };

        var violations = _enforcer.Check(scope, files);

        violations.Should().HaveCount(1);
        violations[0].ViolationType.Should().Be("NotAllowed");
        violations[0].FilePath.Should().Be("src/Orders/OrderService.cs");
    }

    [Fact]
    public void Check_EmptyAllowed_AllowsEverything()
    {
        var scope = new ScopeDefinition
        {
            Allowed = [],
            Forbidden = ["src/Secrets/**"]
        };
        var files = new List<string>
        {
            "src/Anything/File.cs",
            "src/Other/Thing.cs"
        };

        var violations = _enforcer.Check(scope, files);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Check_MultipleViolations_ReturnsAll()
    {
        var scope = new ScopeDefinition
        {
            Allowed = ["src/Customers/**"],
            Forbidden = ["src/Billing/**"]
        };
        var files = new List<string>
        {
            "src/Orders/OrderService.cs",
            "src/Billing/BillingService.cs"
        };

        var violations = _enforcer.Check(scope, files);

        violations.Should().HaveCount(2);
    }

    [Fact]
    public void MatchesGlob_DoubleStar_MatchesSubdirectories()
    {
        ScopeEnforcer.MatchesGlob("src/Customers/CustomerMapper.cs", "src/Customers/**").Should().BeTrue();
        ScopeEnforcer.MatchesGlob("src/Customers/Sub/Deep.cs", "src/Customers/**").Should().BeTrue();
        ScopeEnforcer.MatchesGlob("src/Billing/Billing.cs", "src/Customers/**").Should().BeFalse();
    }

    [Fact]
    public void MatchesGlob_SingleStar_MatchesWithinSegment()
    {
        ScopeEnforcer.MatchesGlob("src/Customers/CustomerMapper.cs", "src/Customers/*.cs").Should().BeTrue();
        ScopeEnforcer.MatchesGlob("src/Customers/CustomerMapper.txt", "src/Customers/*.cs").Should().BeFalse();
    }

    [Fact]
    public void MatchesGlob_CaseInsensitive()
    {
        ScopeEnforcer.MatchesGlob("SRC/Customers/File.cs", "src/Customers/**").Should().BeTrue();
    }
}
