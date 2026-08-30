using AECS.Application.SemanticLinter;
using FluentAssertions;

namespace AECS.UnitTests;

public class EB004MissingChangeVerifierTests
{
    [Fact]
    public void Verify_SourceChangedTestNotChanged_Detected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create source file
            var srcDir = Path.Combine(tempDir, "src", "MyApp", "Services");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "CustomerService.cs"), "namespace MyApp.Services; public class CustomerService { }");

            // Create test file
            var testDir = Path.Combine(tempDir, "tests", "MyApp.Tests");
            Directory.CreateDirectory(testDir);
            File.WriteAllText(Path.Combine(testDir, "CustomerServiceTests.cs"), "namespace MyApp.Tests; public class CustomerServiceTests { }");

            var diff = """
--- a/src/MyApp/Services/CustomerService.cs
+++ b/src/MyApp/Services/CustomerService.cs
@@ -1,1 +1,2 @@
 namespace MyApp.Services;
-public class CustomerService { }
+public class CustomerService
+{
+    public void Create() { }
+}
""";

            var verifier = new EB004MissingChangeVerifier();
            var result = verifier.Verify(tempDir, diff);

            result.HasMissingChanges.Should().BeTrue();
            result.MissingChanges.Should().Contain(mc => mc.RuleId == "EB004-MISSING-TEST");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_SourceAndTestBothChanged_NoViolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var srcDir = Path.Combine(tempDir, "src", "MyApp", "Services");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "CustomerService.cs"), "namespace MyApp.Services; public class CustomerService { }");

            var testDir = Path.Combine(tempDir, "tests", "MyApp.Tests");
            Directory.CreateDirectory(testDir);
            File.WriteAllText(Path.Combine(testDir, "CustomerServiceTests.cs"), "namespace MyApp.Tests; public class CustomerServiceTests { }");

            var diff = """
--- a/src/MyApp/Services/CustomerService.cs
+++ b/src/MyApp/Services/CustomerService.cs
@@ -1,1 +1,2 @@
 namespace MyApp.Services;
-public class CustomerService { }
+public class CustomerService
+{
+    public void Create() { }
+}
--- a/tests/MyApp.Tests/CustomerServiceTests.cs
+++ b/tests/MyApp.Tests/CustomerServiceTests.cs
@@ -1,1 +1,2 @@
 namespace MyApp.Tests;
-public class CustomerServiceTests { }
+public class CustomerServiceTests
+{
+    public void Create_ShouldWork() { }
+}
""";

            var verifier = new EB004MissingChangeVerifier();
            var result = verifier.Verify(tempDir, diff);

            result.HasMissingChanges.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_EmptyDiff_NoViolation()
    {
        var verifier = new EB004MissingChangeVerifier();
        var result = verifier.Verify("/repo", "");

        result.HasMissingChanges.Should().BeFalse();
    }
}

public class EB005HistoricalConflictVerifierTests
{
    [Fact]
    public void Verify_CodeViolatesADR_Detected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var srcDir = Path.Combine(tempDir, "src", "MyApp");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "DecisionMaker.cs"), """
                using MyApp.LLMOnly;

                namespace MyApp;

                public class DecisionMaker
                {
                    public void Decide() { }
                }
                """);

            var decisions = new List<HistoricalDecision>
            {
                new()
                {
                    Id = "ADR-001",
                    Source = "ADR-001",
                    Type = DecisionType.Adr,
                    Description = "Critical rules must be deterministic",
                    ProhibitedPatterns = ["LLMOnly"]
                }
            };

            var verifier = new EB005HistoricalConflictVerifier();
            var result = verifier.Verify(tempDir, decisions);

            result.HasConflicts.Should().BeTrue();
            result.Conflicts.Should().Contain(c => c.RuleId == "EB005-ADR-CONFLICT");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_NoConflict_NoViolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var srcDir = Path.Combine(tempDir, "src", "MyApp");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "GoodCode.cs"), """
                namespace MyApp;

                public class GoodCode
                {
                    public void DoWork() { }
                }
                """);

            var decisions = new List<HistoricalDecision>
            {
                new()
                {
                    Id = "ADR-001",
                    Source = "ADR-001",
                    Type = DecisionType.Adr,
                    Description = "Critical rules must be deterministic",
                    ProhibitedPatterns = ["LLMOnly"]
                }
            };

            var verifier = new EB005HistoricalConflictVerifier();
            var result = verifier.Verify(tempDir, decisions);

            result.HasConflicts.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Verify_IncidentPattern_Detected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aecs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var srcDir = Path.Combine(tempDir, "src", "MyApp");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "RiskyCode.cs"), """
                namespace MyApp;

                public class RiskyCode
                {
                    public void DoWork()
                    {
                        // This pattern caused INC-2024-001
                        var result = DangerousOperation();
                    }
                }
                """);

            var decisions = new List<HistoricalDecision>
            {
                new()
                {
                    Id = "INC-001",
                    Source = "INC-2024-001",
                    Type = DecisionType.Incident,
                    Description = "DangerousOperation caused outage",
                    ProhibitedPatterns = ["DangerousOperation"]
                }
            };

            var verifier = new EB005HistoricalConflictVerifier();
            var result = verifier.Verify(tempDir, decisions);

            result.HasConflicts.Should().BeTrue();
            result.Conflicts.Should().Contain(c => c.RuleId == "EB005-INCIDENT-CONFLICT");
            result.Conflicts.Should().Contain(c => c.Severity == RuleSeverity.Error);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
