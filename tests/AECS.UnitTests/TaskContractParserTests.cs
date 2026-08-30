using AECS.Application.Parsing;
using AECS.Domain.Enums;
using FluentAssertions;
using YamlDotNet.Core;

namespace AECS.UnitTests;

public class TaskContractParserTests
{
    private readonly TaskContractParser _parser = new();

    [Fact]
    public void Parse_ValidYaml_ReturnsTaskContract()
    {
        var yaml = """
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
    }

    [Fact]
    public void Parse_MinimalYaml_UsesDefaults()
    {
        var yaml = """
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
    }

    [Fact]
    public void Parse_HumanApproval_SetsCorrectLevel()
    {
        var yaml = """
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
    }

    [Fact]
    public void Parse_WithoutExecutionProfile_UsesRepositoryRootDefaults()
    {
        var yaml = """
            task:
              id: TASK-DEFAULT
              objective: Use default execution profile
            """;

        var result = _parser.Parse(yaml);

        result.Execution.WorkingDirectory.Should().Be(".");
        result.Execution.Target.Should().BeEmpty();
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
}
