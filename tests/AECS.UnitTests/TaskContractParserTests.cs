using AECS.Application.Parsing;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;
using YamlDotNet.Core;

namespace AECS.UnitTests;

public class TaskContractParserTests
{
    private readonly TaskContractParser _parser = new();

    [Theory]
    [InlineData("retries", "-1", "budget.retries")]
    [InlineData("wall_clock_seconds", "0", "budget.wall_clock_seconds")]
    [InlineData("tokens", "-1", "budget.tokens")]
    [InlineData("usd", "-0.01", "budget.usd")]
    public void Parse_InvalidBudget_IsRejected(
        string field,
        string value,
        string expectedMessage)
    {
        var yaml = $"""
            schema_version: aecs.task-contract/v1
            task:
              id: INVALID-BUDGET
              objective: Reject invalid limits
              budget:
                {field}: {value}
            """;

        var action = () => _parser.Parse(yaml);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void Parse_ValidYaml_ReturnsTaskContract()
    {
        var yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-001
              objective: Fix null handling
              acceptance:
                - No null exceptions
                - Tests pass
              scope:
                allowed:
                  - src/Customers/**
                forbidden:
                  - src/Billing/**
              constraints:
                security_risk: low
                database_migration: false
                external_dependency: false
              budget:
                tokens: 60000
                usd: 0.20
                retries: 1
                wall_clock_seconds: 120
                max_files_changed: 5
              verification:
                build: required
                unit_tests: required
                scope: required
              approval:
                production: none
            """;

        var result = _parser.Parse(yaml);

        result.Id.Should().Be("TASK-001");
        result.Objective.Should().Be("Fix null handling");
        result.AcceptanceCriteria.Should().HaveCount(2);
        result.AcceptanceRequirements.Should().HaveCount(2);
        result.AcceptanceRequirements.Should().OnlyContain(criterion =>
            criterion.Evidence.Type == AcceptanceEvidenceType.None);
        result.Scope.Allowed.Should().Contain("src/Customers/**");
        result.Scope.Forbidden.Should().Contain("src/Billing/**");
        result.Budget.MaxTokens.Should().Be(60000);
        result.Budget.MaxCostUsd.Should().Be(0.20m);
        result.Budget.MaxRetries.Should().Be(1);
        result.Budget.MaxDurationSeconds.Should().Be(120);
        result.Verification.Build.Should().BeTrue();
        result.Verification.UnitTests.Should().BeTrue();
        result.Approval.Production.Should().Be(ApprovalLevel.None);
        result.Status.Should().Be(TaskState.ContractReady);
        result.SchemaVersion.Should().Be(TaskContractSchema.CurrentVersion);
        result.ContractFingerprint.Should().MatchRegex("^sha256:[a-f0-9]{64}$");
        TaskContractIntegrity.ValidateForEvidence(result);
    }

    [Fact]
    public void Parse_AcceptanceEvidence_MapsTestsVerifiersAndSemanticPolicy()
    {
        var yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-ACCEPTANCE
              objective: Prevent null names
              acceptance:
                - Null name is rejected
                - Existing regression suite passes
              acceptance_evidence:
                - id: AC-001
                  type: test
                  reference: FullyQualifiedName~Create_NullName
                  test_path: tests/CreateHandlerTests.cs
                  behavioral: true
                - criterion: Existing regression suite passes
                  type: verifier
                  reference: Tests
              verification:
                critical_semantic_failures: required
                required_semantic_verifiers:
                  - EB003-BreakingChange
            """;

        var result = _parser.Parse(yaml);

        result.AcceptanceRequirements.Should().HaveCount(2);
        result.AcceptanceRequirements[0].Id.Should().Be("AC-001");
        result.AcceptanceRequirements[0].Behavioral.Should().BeTrue();
        result.AcceptanceRequirements[0].Evidence.Type.Should().Be(AcceptanceEvidenceType.Test);
        result.AcceptanceRequirements[0].Evidence.Reference.Should()
            .Be("FullyQualifiedName~Create_NullName");
        result.AcceptanceRequirements[0].Evidence.TestPath.Should()
            .Be("tests/CreateHandlerTests.cs");
        result.AcceptanceRequirements[1].Evidence.Type.Should()
            .Be(AcceptanceEvidenceType.Verifier);
        result.AcceptanceRequirements[1].Evidence.Reference.Should().Be("Tests");
        result.Verification.BlockCriticalSemanticFailures.Should().BeTrue();
        result.Verification.RequiredSemanticVerifiers.Should()
            .ContainSingle("EB003-BreakingChange");
    }

    [Fact]
    public void Parse_UnboundAcceptanceEvidence_RejectsAmbiguousContract()
    {
        var yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-INVALID
              objective: Invalid mapping
              acceptance:
                - Known criterion
              acceptance_evidence:
                - id: AC-999
                  type: verifier
                  reference: Tests
            """;

        var action = () => _parser.Parse(yaml);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match*criterion*");
    }

    [Fact]
    public void Parse_MinimalYaml_UsesDefaults()
    {
        var yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-MINIMAL
              objective: Simple change
            """;

        var result = _parser.Parse(yaml);

        result.Id.Should().Be("TASK-MINIMAL");
        result.Objective.Should().Be("Simple change");
        result.AcceptanceCriteria.Should().BeEmpty();
        result.Scope.Allowed.Should().BeEmpty();
        result.Scope.Forbidden.Should().BeEmpty();
        result.Budget.MaxTokens.Should().Be(60000);
        result.Budget.MaxCostUsd.Should().Be(0.20m);
        result.Verification.Build.Should().BeTrue();
        result.Approval.Production.Should().Be(ApprovalLevel.None);
        result.Execution.Runtime.Should().Be(RepositoryExecutionProfile.DockerRuntime);
        result.Execution.Sandbox.Should().NotBeNull();
        result.Execution.Sandbox!.Image.Should().Contain("@sha256:");
        result.Execution.Capabilities!.Version.Should()
            .Be(ExecutionCapabilityPolicy.CurrentVersion);
        result.Execution.Capabilities.FileSystem.Read.Should().Equal("**");
        result.Execution.Capabilities.Processes.Should().Contain(rule =>
            rule.Executable == "dotnet" &&
            rule.ArgumentPrefix.Count == 1 &&
            rule.ArgumentPrefix[0] == "build");
    }

    [Fact]
    public void Parse_HumanApproval_SetsCorrectLevel()
    {
        var yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-HUMAN
              objective: Critical change
              approval:
                production: human
            """;

        var result = _parser.Parse(yaml);

        result.Approval.Production.Should().Be(ApprovalLevel.Human);
    }

    [Fact]
    public void Parse_ExecutionProfile_MapsNestedSolution()
    {
        var yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-NESTED
              objective: Verify a nested solution
              execution:
                working_directory: Backend
                target: AgronomoPlus.sln
            """;

        var result = _parser.Parse(yaml);

        result.Execution.WorkingDirectory.Should().Be("Backend");
        result.Execution.Target.Should().Be("AgronomoPlus.sln");
        result.Execution.EffectiveRuntime.Should().Be(RepositoryExecutionProfile.DockerRuntime);
    }

    [Fact]
    public void Parse_WithoutExecutionProfile_UsesRepositoryRootDefaults()
    {
        var yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-DEFAULT
              objective: Use default execution profile
            """;

        var result = _parser.Parse(yaml);

        result.Execution.WorkingDirectory.Should().Be(".");
        result.Execution.Target.Should().BeEmpty();
        result.Execution.Runtime.Should().Be(RepositoryExecutionProfile.DockerRuntime);
    }

    [Fact]
    public void Parse_ExplicitHostRuntime_MarksDevelopmentModeWithoutSandbox()
    {
        var result = _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-HOST
              objective: Local development
              execution:
                runtime: host
                target: Fixture.csproj
            """);

        result.Execution.Runtime.Should().Be(RepositoryExecutionProfile.HostRuntime);
        result.Execution.Sandbox.Should().BeNull();
    }

    [Theory]
    [InlineData("image", "mcr.microsoft.com/dotnet/sdk:10.0", "pinned by sha256")]
    [InlineData("cpu_limit", "0", "cpu_limit")]
    [InlineData("memory_limit", "0m", "memory_limit")]
    [InlineData("process_limit", "0", "process_limit")]
    [InlineData("wall_clock_seconds", "0", "wall_clock_seconds")]
    public void Parse_InvalidDockerSandbox_FailsClosed(
        string field,
        string value,
        string expectedMessage)
    {
        var yaml = $"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-BAD-SANDBOX
              objective: Reject weak isolation
              execution:
                target: Fixture.csproj
                sandbox:
                  {field}: {value}
            """;

        var action = () => _parser.Parse(yaml);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void Parse_VersionedCapabilities_MapsEveryLeastPrivilegeDimension()
    {
        var result = _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-CAPABILITIES
              objective: Run one authenticated build
              execution:
                target: Fixture.csproj
                sandbox:
                  cpu_limit: "0.5"
                  memory_limit: 256m
                  process_limit: 32
                  wall_clock_seconds: 60
                  network_access: true
              capabilities:
                version: aecs.capabilities/v1
                file_system:
                  read: ["**"]
                  write: ["**/bin/**", "**/obj/**"]
                processes:
                  - executable: git
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["build"]
                    phases: ["baseline.build", "candidate.build"]
                network:
                  destinations: ["*"]
                  phases: ["baseline.build"]
                secrets:
                  - name: NUGET_AUTH_TOKEN
                    phases: ["baseline.build"]
                resources:
                  cpu_limit: "0.75"
                  memory_limit: 384m
                  process_limit: 64
                  wall_clock_seconds: 90
              verification:
                unit_tests: optional
            """);

        var capabilities = result.Execution.Capabilities!;
        capabilities.Version.Should().Be("aecs.capabilities/v1");
        capabilities.Authority.Should().Be("task-contract");
        capabilities.FileSystem.Write.Should().Equal("**/bin/**", "**/obj/**");
        capabilities.Processes.Should().HaveCount(3);
        capabilities.Network.Destinations.Should().Equal("*");
        capabilities.Secrets.Should().ContainSingle(secret =>
            secret.Name == "NUGET_AUTH_TOKEN");
        capabilities.Resources.MemoryLimit.Should().Be("384m");
    }

    [Theory]
    [InlineData("version: future/v9", "Unsupported capabilities.version")]
    [InlineData("file_system:\n      read: [\"../outside\"]", "file_system.read")]
    [InlineData("file_system:\n      read: [\"**\"]\n      write: [\"../outside/**\"]", "safe workspace")]
    [InlineData("file_system:\n      read: [\"**\"]\n      write: [\"artifacts,readonly/**\"]", "safe workspace")]
    [InlineData("file_system:\n  read: [\"**\"]\nprocesses:\n  - executable: /bin/sh\n    argument_prefix: [\"-c\"]\n    phases: [\"candidate.build\"]", "executable")]
    [InlineData("file_system:\n  read: [\"**\"]\nsecrets:\n  - name: bad-secret\n    phases: [\"candidate.build\"]", "secret capability name")]
    public void Parse_InvalidCapabilities_FailClosed(string fragment, string expected)
    {
        var indented = fragment.Replace("\n", "\n    ");
        var yaml = $"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-BAD-CAP
              objective: Reject invalid capabilities
              capabilities:
                {indented}
            """;

        var action = () => _parser.Parse(yaml);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Parse_SandboxResourcesAboveCapabilityMaximum_FailsClosed()
    {
        var action = () => _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-RESOURCE-EXPANSION
              objective: Reject resource expansion
              execution:
                sandbox:
                  memory_limit: 1g
              capabilities:
                file_system:
                  read: ["**"]
                resources:
                  memory_limit: 256m
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*resource limits exceed*capabilities.resources maxima*");
    }

    [Fact]
    public void Parse_MissingProcessCapabilityRequiredByGate_FailsBeforeExecution()
    {
        var action = () => _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-MISSING-PROCESS
              objective: Reject a policy that cannot execute the required test gate
              capabilities:
                file_system:
                  read: ["**"]
                processes:
                  - executable: git
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["build"]
                    phases: ["baseline.build", "candidate.build"]
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Capabilities deny required preflight command*baseline.test: dotnet test*");
    }

    [Fact]
    public void Parse_RequiredSecurityScan_MapsVersionedPolicyAndSuppression()
    {
        var result = _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-SECURITY-SCAN
              objective: Apply a deterministic security gate
              execution:
                target: Fixture.csproj
              verification:
                security_scan: required
                security_policy:
                  version: aecs.security-scan/v1
                  scanners: [secrets, dependencies, patterns]
                  block_at_or_above: critical
                  vulnerability_database_version: aecs.nuget-advisories/2026-08-31
                  suppressions:
                    - rule: AECS-PATTERN-WEAK-HASH
                      path: src/LegacyHash.cs
                      justification: Legacy checksum is not used for a security decision.
            """);

        result.Verification.SecurityScan.Should().BeTrue();
        var policy = result.Verification.SecurityPolicy!;
        policy.Version.Should().Be(SecurityScanSchema.PolicyVersion);
        policy.Scanners.Should().Equal("secrets", "dependencies", "patterns");
        policy.BlockAtOrAbove.Should().Be(Severity.Critical);
        policy.Suppressions.Should().ContainSingle(suppression =>
            suppression.Rule == "AECS-PATTERN-WEAK-HASH" &&
            suppression.Path == "src/LegacyHash.cs");
        result.Execution.EffectiveCapabilities.Processes.Should().Contain(rule =>
            rule.Executable == "dotnet" &&
            rule.ArgumentPrefix.Count == 1 &&
            rule.ArgumentPrefix[0] == "list" &&
            rule.Phases.Contains(ExecutionCapabilityPhases.BaselineSecurityScan) &&
            rule.Phases.Contains(ExecutionCapabilityPhases.CandidateSecurityScan));
    }

    [Theory]
    [InlineData("version: future/v2", "Unsupported security scan policy version")]
    [InlineData("scanners: [secrets, unknown]", "supported scanner IDs")]
    [InlineData("vulnerability_database_version: moving-latest", "vulnerability database")]
    [InlineData("suppressions:\n  - rule: RULE\n    path: ../outside\n    justification: justified false positive", "suppressions require")]
    [InlineData("suppressions:\n  - rule: RULE\n    path: src/File.cs\n    justification: short", "suppressions require")]
    public void Parse_InvalidSecurityScanPolicy_FailsClosed(string fragment, string expected)
    {
        var indented = fragment.Replace("\n", "\n        ");
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-BAD-SECURITY-POLICY
              objective: Reject an invalid security scan policy
              verification:
                security_scan: required
                security_policy:
                  {indented}
            """);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Parse_SecurityScanWithoutDotNetListCapability_FailsPreflight()
    {
        var action = () => _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-MISSING-SCAN-CAPABILITY
              objective: Reject missing scanner capability
              capabilities:
                file_system:
                  read: ["**"]
                processes:
                  - executable: git
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["build"]
                    phases: ["baseline.build", "candidate.build"]
                  - executable: dotnet
                    argument_prefix: ["test"]
                    phases: ["baseline.test", "candidate.test"]
              verification:
                security_scan: required
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*required preflight command*baseline.security-scan: dotnet list*");
    }

    [Fact]
    public void Parse_SecurityScanWithoutDependencyScanner_DoesNotRequireDotNetListCapability()
    {
        var result = _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-SECRET-SCAN-ONLY
              objective: Run only the built-in secret scanner
              capabilities:
                file_system:
                  read: ["**"]
                processes:
                  - executable: git
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["--version"]
                    phases: ["baseline.tool-probe"]
                  - executable: dotnet
                    argument_prefix: ["build"]
                    phases: ["baseline.build", "candidate.build"]
                  - executable: dotnet
                    argument_prefix: ["test"]
                    phases: ["baseline.test", "candidate.test"]
              verification:
                security_scan: required
                security_policy:
                  scanners: [secrets]
            """);

        result.Verification.EffectiveSecurityPolicy.Scanners.Should().Equal("secrets");
        result.Execution.EffectiveCapabilities.Processes.Should().NotContain(rule =>
            rule.Executable == "dotnet" && rule.ArgumentPrefix.Contains("list"));
    }

    [Fact]
    public void Parse_VersionedTestSuites_MapsIndependentModesTargetsAndTimeouts()
    {
        var result = _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-INDEPENDENT-TESTS
              objective: Run separate test projects
              execution:
                target: Product.sln
                test_suites:
                  version: aecs.test-suites/v1
                  unit:
                    mode: required
                    target: tests/Unit/Product.UnitTests.csproj
                    arguments: ["--configuration", "Release"]
                    timeout_seconds: 30
                  integration:
                    mode: optional
                    target: tests/Integration/Product.IntegrationTests.csproj
                    timeout_seconds: 45
                  acceptance:
                    mode: disabled
              verification:
                unit_tests: optional
                integration_tests: required
            """);

        var suites = result.Execution.TestSuites!;
        suites.Version.Should().Be(TestSuiteSchema.ProfileVersion);
        suites.Unit.Mode.Should().Be(TestGateMode.Required);
        suites.Unit.Target.Should().Be("tests/Unit/Product.UnitTests.csproj");
        suites.Unit.Arguments.Should().Equal("--configuration", "Release");
        suites.Unit.TimeoutSeconds.Should().Be(30);
        suites.Integration.Mode.Should().Be(TestGateMode.Optional);
        suites.Integration.TimeoutSeconds.Should().Be(45);
        suites.Acceptance.Mode.Should().Be(TestGateMode.Disabled);
        result.Verification.UnitTests.Should().BeTrue(
            "the v1 suite mode is authoritative over the legacy verification field");
        result.Verification.IntegrationTests.Should().BeFalse();
        result.Execution.EffectiveCapabilities.Processes.Should().Contain(rule =>
            rule.Executable == "dotnet" &&
            rule.ArgumentPrefix.Count == 1 &&
            rule.ArgumentPrefix[0] == "test" &&
            rule.Phases.Contains(ExecutionCapabilityPhases.BaselineUnitTest) &&
            rule.Phases.Contains(ExecutionCapabilityPhases.CandidateIntegrationTest));
    }

    [Theory]
    [InlineData("version: future/v2", "Unsupported execution.test_suites version")]
    [InlineData("unit:\n  mode: sometimes\n  target: tests/Unit.csproj", "Unknown test suite mode")]
    [InlineData("unit:\n  mode: required\n  target: ../outside.csproj", "safe relative")]
    [InlineData("unit:\n  mode: required\n  target: tests/Unit.csproj\n  timeout_seconds: 0", "timeout_seconds must be positive")]
    [InlineData("unit:\n  mode: required\n  target: tests/Unit.csproj\n  arguments: [\"--logger\"]", "controller-owned argument")]
    public void Parse_InvalidVersionedTestSuite_FailsClosed(string fragment, string expected)
    {
        var indented = fragment.Replace("\n", "\n        ");
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-BAD-TEST-SUITE
              objective: Reject an invalid test suite profile
              execution:
                target: Product.sln
                test_suites:
                  {indented}
            """);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Parse_TestBasedAcceptanceWithDisabledAcceptanceSuite_FailsClosed()
    {
        var action = () => _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-DISABLED-ACCEPTANCE-SUITE
              objective: Reject acceptance evidence without an executable suite
              acceptance:
                - The targeted behavior passes
              acceptance_evidence:
                - id: AC-001
                  type: test
                  reference: FullyQualifiedName~TargetedBehavior
              execution:
                target: Product.sln
                test_suites:
                  version: aecs.test-suites/v1
                  unit:
                    mode: required
                    target: tests/Unit/Product.UnitTests.csproj
                  acceptance:
                    mode: disabled
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires an enabled acceptance test suite*");
    }

    [Fact]
    public void Parse_LegacyTestFields_PreserveAggregateTestsCompatibility()
    {
        var result = _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-LEGACY-TESTS
              objective: Preserve the aggregate gate
              verification:
                unit_tests: required
                integration_tests: optional
            """);

        result.Execution.TestSuites.Should().BeNull();
        result.Verification.UnitTests.Should().BeTrue();
        result.Verification.IntegrationTests.Should().BeFalse();
    }

    [Fact]
    public void Parse_RepositorySnapshotProfile_MapsVersionAndSafeExclusions()
    {
        var result = _parser.Parse("""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-REPOSITORY-SNAPSHOT
              objective: Configure deterministic repository discovery
              execution:
                repository_snapshot:
                  version: aecs.repository-snapshot-profile/v1
                  excluded_directories:
                    - vendor/generated/
                    - docs/archive
            """);

        result.Execution.RepositorySnapshot.Should().NotBeNull();
        result.Execution.RepositorySnapshot!.Version.Should()
            .Be(RepositorySnapshotSchema.ProfileVersion);
        result.Execution.RepositorySnapshot.ExcludedDirectories.Should()
            .Equal("docs/archive", "vendor/generated");
    }

    [Theory]
    [InlineData("version: future/v2", "Unsupported execution.repository_snapshot version")]
    [InlineData("excluded_directories: [\"../outside\"]", "safe relative directory")]
    [InlineData("excluded_directories: [\"C:/outside\"]", "safe relative directory")]
    [InlineData("excluded_directories: [\"nested/.git/objects\"]", "safe relative directory")]
    public void Parse_InvalidRepositorySnapshotProfile_FailsClosed(
        string fragment,
        string expected)
    {
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-BAD-REPOSITORY-SNAPSHOT
              objective: Reject unsafe snapshot configuration
              execution:
                repository_snapshot:
                  {fragment}
            """);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Theory]
    [InlineData("", "must declare schema_version")]
    [InlineData("schema_version: aecs.task-contract/v2\n", "Unsupported TaskContract schema_version")]
    public void Parse_MissingOrUnsupportedSchema_FailsClosed(
        string schema,
        string expected)
    {
        var action = () => _parser.Parse($"""
            {schema}task:
              id: TASK-SCHEMA
              objective: Validate the contract schema
            """);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Theory]
    [InlineData("task.budegt", "budegt: {}")]
    [InlineData("task.budget.toknes", "budget:\n    toknes: 100")]
    [InlineData("task.execution.sandbox.memroy_limit", "execution:\n    sandbox:\n      memroy_limit: 512m")]
    public void Parse_UnknownProperties_ReportTheirFullPath(
        string expectedPath,
        string fragment)
    {
        var indented = fragment.Replace("\n", "\n  ");
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-TYPO
              objective: Reject misspelled policy fields
              {indented}
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*$.{expectedPath}*");
    }

    [Fact]
    public void Parse_ConflictingAliases_FailsClosed()
    {
        var action = () => _parser.Parse("""
            schema_version: aecs.task-contract/v1
            SchemaVersion: aecs.task-contract/v1
            task:
              id: TASK-ALIASES
              objective: Reject conflicting aliases
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*conflicting aliases*");
    }

    [Theory]
    [InlineData("id", "")]
    [InlineData("objective", "")]
    public void Parse_EmptyRequiredText_FailsClosed(string field, string value)
    {
        var id = field == "id" ? value : "TASK-REQUIRED";
        var objective = field == "objective" ? value : "Validate required values";
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: {id}
              objective: {objective}
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*task.{field} is required*");
    }

    [Theory]
    [InlineData("id", TaskContractIntegrity.MaximumIdLength + 1)]
    [InlineData("objective", TaskContractIntegrity.MaximumObjectiveLength + 1)]
    public void Parse_RequiredTextAboveLimit_FailsClosed(string field, int length)
    {
        var id = field == "id" ? new string('I', length) : "TASK-BOUNDS";
        var objective = field == "objective"
            ? new string('O', length)
            : "Validate required value limits";
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: {id}
              objective: {objective}
            """);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*task.{field} cannot exceed*");
    }

    [Theory]
    [InlineData("r0", RiskLevel.R0)]
    [InlineData("low", RiskLevel.R1)]
    [InlineData("r1", RiskLevel.R1)]
    [InlineData("medium", RiskLevel.R2)]
    [InlineData("r2", RiskLevel.R2)]
    [InlineData("high", RiskLevel.R3)]
    [InlineData("r3", RiskLevel.R3)]
    [InlineData("r4", RiskLevel.R4)]
    public void Parse_DeclaredRisk_IsMappedExactly(string value, RiskLevel expected)
    {
        var result = _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-RISK
              objective: Preserve explicitly declared risk
              constraints:
                security_risk: {value}
            """);

        result.Constraints.SecurityRisk.Should().Be(expected);
    }

    [Theory]
    [InlineData("constraints:\n    security_risk: r5", "constraints.security_risk")]
    [InlineData("verification:\n    build: sometimes", "verification mode")]
    [InlineData("approval:\n    production: automatic", "approval.production")]
    public void Parse_UnknownPolicyValues_FailClosed(string fragment, string expected)
    {
        var indented = fragment.Replace("\n", "\n  ");
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-POLICY
              objective: Reject unknown policy values
              {indented}
            """);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Theory]
    [InlineData("execution:\n    runtime: future", "execution.runtime")]
    [InlineData("capabilities:\n    file_system:\n      read: [\"**\"]\n    processes:\n      - executable: git\n        argument_prefix: [\"--version\"]\n        phases: [\"future.phase\"]", "unknown phase")]
    [InlineData("acceptance:\n  - It works\nacceptance_evidence:\n  - id: AC-001\n    type: future", "acceptance evidence type")]
    public void Parse_UnknownEnumeratedValues_FailClosed(string fragment, string expected)
    {
        var indented = fragment.Replace("\n", "\n  ");
        var action = () => _parser.Parse($"""
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-FUTURE-VALUE
              objective: Reject values outside closed enumerations
              {indented}
            """);

        action.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Parse_SameSemanticContract_ProducesStableFingerprint()
    {
        const string yaml = """
            schema_version: aecs.task-contract/v1
            task:
              id: TASK-FINGERPRINT
              objective: Produce a stable semantic fingerprint
              constraints:
                security_risk: r4
              approval:
                production: human
            """;

        var first = _parser.Parse(yaml);
        var second = _parser.Parse(yaml);

        first.ContractFingerprint.Should().Be(second.ContractFingerprint);
    }

    [Fact]
    public void Parse_AllTrackedTaskContracts_AreValidCurrentSchema()
    {
        var root = FindRepositoryRoot();
        var searchRoots = new[]
        {
            Path.Combine(root, "tasks"),
            Path.Combine(root, "experiments"),
            Path.Combine(root, "tests", "fixtures")
        };
        var contracts = searchRoots
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(
                path,
                "*.yaml",
                SearchOption.AllDirectories))
            .Where(path => File.ReadLines(path).Any(line => line == "task:"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        contracts.Should().HaveCount(22);
        foreach (var path in contracts)
        {
            TaskContract contract;
            try
            {
                contract = _parser.ParseFromFile(path);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Tracked contract '{Path.GetRelativePath(root, path)}' is invalid.",
                    exception);
            }
            contract.SchemaVersion.Should().Be(
                TaskContractSchema.CurrentVersion,
                $"{Path.GetRelativePath(root, path)} must use the current schema");
            TaskContractIntegrity.ValidateForEvidence(contract);
        }
    }

    [Fact]
    public void Parse_MissingTaskKey_ThrowsYamlException()
    {
        var yaml = "not_a_task: true";

        var act = () => _parser.Parse(yaml);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ParseFromFile_ValidFile_ReturnsContract()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tasks", "task-001-fix-null.yaml");
        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            // Skip if running in CI without the tasks directory
            return;
        }

        var result = _parser.ParseFromFile(fullPath);

        result.Id.Should().Be("TASK-001");
        result.Objective.Should().Contain("NullReferenceException");
        result.Scope.Allowed.Should().Contain("src/Customers/**");
        result.Scope.Forbidden.Should().Contain("src/Billing/**");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "AECS.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Unable to find the AECS repository root from the test output directory.");
    }
}
