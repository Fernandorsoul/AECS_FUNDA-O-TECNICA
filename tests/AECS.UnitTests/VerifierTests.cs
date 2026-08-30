using AECS.Application.ControlKernel;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class ScopeVerifierTests
{
    private readonly ScopeVerifier _verifier = new();

    [Fact]
    public async Task VerifyAsync_FilesInScope_ReturnsPass()
    {
        var context = new VerificationContext
        {
            TaskId = "T1",
            AgentRunId = "R1",
            RepoPath = "/tmp",
            Contract = new TaskContract
            {
                Scope = new ScopeDefinition
                {
                    Allowed = ["src/Customers/**", "tests/Customers/**"]
                }
            },
            AgentResult = new AgentRunResult
            {
                FilesChanged = ["src/Customers/CustomerMapper.cs", "tests/Customers/CustomerMapperTests.cs"]
            },
            CandidateChangeSet = new CandidateChangeSet
            {
                AddedFiles = ["src/Customers/CustomerMapper.cs", "tests/Customers/CustomerMapperTests.cs"],
                Diff = "diff"
            }
        };

        var result = await _verifier.VerifyAsync(context, CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Pass);
        result.Verifier.Should().Be("Scope");
    }

    [Fact]
    public async Task VerifyAsync_FileOutOfScope_ReturnsFail()
    {
        var context = new VerificationContext
        {
            TaskId = "T1",
            AgentRunId = "R1",
            RepoPath = "/tmp",
            Contract = new TaskContract
            {
                Scope = new ScopeDefinition
                {
                    Allowed = ["src/Customers/**"],
                    Forbidden = ["src/Billing/**"]
                }
            },
            AgentResult = new AgentRunResult
            {
                FilesChanged = ["src/Billing/BillingService.cs"]
            },
            CandidateChangeSet = new CandidateChangeSet
            {
                AddedFiles = ["src/Billing/BillingService.cs"],
                Diff = "diff"
            }
        };

        var result = await _verifier.VerifyAsync(context, CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Message.Should().Contain("Scope violations");
    }

    [Fact]
    public async Task VerifyAsync_IgnoresAgentTelemetry_AndUsesCandidateChangeSet()
    {
        var context = new VerificationContext
        {
            TaskId = "T1",
            AgentRunId = "R1",
            Contract = new TaskContract
            {
                Scope = new ScopeDefinition { Allowed = ["src/Allowed/**"] }
            },
            AgentResult = new AgentRunResult
            {
                FilesChanged = ["src/Allowed/claimed.cs"]
            },
            CandidateChangeSet = new CandidateChangeSet
            {
                AddedFiles = ["src/Forbidden/actual.cs"],
                Diff = "diff"
            }
        };

        var result = await _verifier.VerifyAsync(context, CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Message.Should().Contain("actual.cs");
    }
}

public class BudgetVerifierTests
{
    private readonly BudgetVerifier _verifier = new();

    [Fact]
    public async Task VerifyAsync_WithinBudget_ReturnsPass()
    {
        var context = new VerificationContext
        {
            TaskId = "T1",
            AgentRunId = "R1",
            Contract = new TaskContract
            {
                Budget = ExecutionBudget.Default
            },
            AgentResult = new AgentRunResult
            {
                InputTokens = 100,
                OutputTokens = 100,
                EstimatedCost = 0.05m,
                Duration = TimeSpan.FromSeconds(30),
                FilesChanged = ["src/Test.cs"]
            },
            CandidateChangeSet = new CandidateChangeSet
            {
                AddedFiles = ["src/Test.cs"],
                Diff = "diff"
            }
        };

        var result = await _verifier.VerifyAsync(context, CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Pass);
        result.Verifier.Should().Be("Budget");
    }

    [Fact]
    public async Task VerifyAsync_BudgetExceeded_ReturnsFail()
    {
        var context = new VerificationContext
        {
            TaskId = "T1",
            AgentRunId = "R1",
            Contract = new TaskContract
            {
                Budget = new ExecutionBudget { MaxTokens = 100, MaxCostUsd = 1m, MaxRetries = 5, MaxDurationSeconds = 300, MaxFilesChanged = 20 }
            },
            AgentResult = new AgentRunResult
            {
                InputTokens = 80,
                OutputTokens = 30,
                EstimatedCost = 0.05m,
                Duration = TimeSpan.FromSeconds(30),
                FilesChanged = ["src/Test.cs"]
            },
            CandidateChangeSet = new CandidateChangeSet
            {
                AddedFiles = ["src/Test.cs"],
                Diff = "diff"
            }
        };

        var result = await _verifier.VerifyAsync(context, CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Message.Should().Contain("Token limit exceeded");
    }
}
