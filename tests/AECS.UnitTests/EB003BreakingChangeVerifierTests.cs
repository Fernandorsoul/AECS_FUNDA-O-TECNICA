using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public class EB003BreakingChangeVerifierTests
{
    [Fact]
    public void Verify_RemovedPublicMethod_Detected()
    {
        var diff = """
            --- a/src/MyApp/Services/CustomerService.cs
            +++ b/src/MyApp/Services/CustomerService.cs
            @@ -10,7 +10,3 @@ namespace MyApp.Services
                 public class CustomerService
                 {
            -        public void CreateCustomer(string name)
            -        {
            -            // implementation
            -        }
                 }
            """;

        var verifier = new EB003BreakingChangeVerifier();
        var result = verifier.Verify("/repo", diff);

        result.HasBreakingChanges.Should().BeTrue();
        result.BreakingChanges.Should().Contain(bc => bc.RuleId == "EB003-REMOVED-METHOD");
        result.BreakingChanges.Should().Contain(bc => bc.SymbolName == "CreateCustomer");
    }

    [Fact]
    public void Verify_RemovedPublicProperty_Detected()
    {
        var diff = """
            --- a/src/MyApp/Models/Customer.cs
            +++ b/src/MyApp/Models/Customer.cs
            @@ -5,4 +5,3 @@ namespace MyApp.Models
                 public class Customer
                 {
            -        public string Name { get; set; }
                     public string Email { get; set; }
                 }
            """;

        var verifier = new EB003BreakingChangeVerifier();
        var result = verifier.Verify("/repo", diff);

        result.HasBreakingChanges.Should().BeTrue();
        result.BreakingChanges.Should().Contain(bc => bc.RuleId == "EB003-REMOVED-PROPERTY");
        result.BreakingChanges.Should().Contain(bc => bc.SymbolName == "Name");
    }

    [Fact]
    public void Verify_RemovedClass_Detected()
    {
        var diff = """
            --- a/src/MyApp/Services/OldService.cs
            +++ b/src/MyApp/Services/OldService.cs
            @@ -1,5 +0,0 @@
            -namespace MyApp.Services;
            -
            -public class OldService
            -{
            -}
            """;

        var verifier = new EB003BreakingChangeVerifier();
        var result = verifier.Verify("/repo", diff);

        result.HasBreakingChanges.Should().BeTrue();
        result.BreakingChanges.Should().Contain(bc => bc.RuleId == "EB003-REMOVED-CLASS");
        result.BreakingChanges.Should().Contain(bc => bc.Severity == RuleSeverity.Critical);
    }

    [Fact]
    public void Verify_MultipleBreakingChanges_AllDetected()
    {
        var diff = """
--- a/src/MyApp/Services/CustomerService.cs
+++ b/src/MyApp/Services/CustomerService.cs
@@ -10,7 +10,3 @@ namespace MyApp.Services
     public class CustomerService
     {
-        public void CreateCustomer(string name)
-        {
-            // implementation
-        }
-        public string GetName(int id)
-        {
-            return "";
-        }
     }
""";

        var verifier = new EB003BreakingChangeVerifier();
        var result = verifier.Verify("/repo", diff);

        result.HasBreakingChanges.Should().BeTrue();
        result.BreakingChanges.Should().HaveCount(2);
        result.BreakingChanges.Should().Contain(bc => bc.SymbolName == "CreateCustomer");
        result.BreakingChanges.Should().Contain(bc => bc.SymbolName == "GetName");
    }

    [Fact]
    public void Verify_MethodRenamed_DetectedAsRemoveAndAdd()
    {
        var diff = """
            --- a/src/MyApp/Services/CustomerService.cs
            +++ b/src/MyApp/Services/CustomerService.cs
            @@ -10,4 +10,4 @@ namespace MyApp.Services
                 public class CustomerService
                 {
            -        public void CreateCustomer(string name)
            +        public void AddCustomer(string name)
                     {
                     }
                 }
            """;

        var verifier = new EB003BreakingChangeVerifier();
        var result = verifier.Verify("/repo", diff);

        result.HasBreakingChanges.Should().BeTrue();
        result.BreakingChanges.Should().Contain(bc => bc.RuleId == "EB003-REMOVED-METHOD");
        result.BreakingChanges.Should().Contain(bc => bc.SymbolName == "CreateCustomer");
    }

    [Fact]
    public void Verify_NoBreakingChanges_NoViolation()
    {
        var diff = """
            --- a/src/MyApp/Services/CustomerService.cs
            +++ b/src/MyApp/Services/CustomerService.cs
            @@ -10,4 +10,8 @@ namespace MyApp.Services
                 public class CustomerService
                 {
                     public void Create(string name) { }
            +
            +        public void Update(string name)
            +        {
            +        }
                 }
            """;

        var verifier = new EB003BreakingChangeVerifier();
        var result = verifier.Verify("/repo", diff);

        result.HasBreakingChanges.Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyDiff_NoViolation()
    {
        var verifier = new EB003BreakingChangeVerifier();
        var result = verifier.Verify("/repo", "");

        result.HasBreakingChanges.Should().BeFalse();
    }
}
