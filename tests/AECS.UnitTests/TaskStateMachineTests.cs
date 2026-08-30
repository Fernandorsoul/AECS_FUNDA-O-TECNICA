using AECS.Domain.Enums;
using AECS.Domain.Exceptions;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class TaskStateMachineTests
{
    [Fact]
    public void NewMachine_StartsInCreatedState()
    {
        var machine = new TaskStateMachine();

        machine.CurrentState.Should().Be(TaskState.Created);
        machine.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Created_CanTransitionTo_ContractReady()
    {
        var machine = new TaskStateMachine();

        machine.TransitionTo(TaskState.ContractReady);

        machine.CurrentState.Should().Be(TaskState.ContractReady);
    }

    [Fact]
    public void Created_CanTransitionTo_Cancelled()
    {
        var machine = new TaskStateMachine();

        machine.TransitionTo(TaskState.Cancelled);

        machine.CurrentState.Should().Be(TaskState.Cancelled);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Created_CannotTransitionTo_Running()
    {
        var machine = new TaskStateMachine();

        var act = () => machine.TransitionTo(TaskState.Running);

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*Created*Running*");
    }

    [Fact]
    public void HappyPath_FullFlow_Created_To_Verified()
    {
        var machine = new TaskStateMachine();

        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.BaselineVerifying);
        machine.TransitionTo(TaskState.Running);
        machine.TransitionTo(TaskState.CandidateProduced);
        machine.TransitionTo(TaskState.Verifying);
        machine.TransitionTo(TaskState.Verified);

        machine.CurrentState.Should().Be(TaskState.Verified);
        machine.IsTerminal.Should().BeTrue();
        machine.History.Should().HaveCount(7);
    }

    [Fact]
    public void BaselineVerifying_CanRejectBeforeRunning()
    {
        var machine = new TaskStateMachine();
        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.BaselineVerifying);

        machine.TransitionTo(TaskState.Rejected);

        machine.CurrentState.Should().Be(TaskState.Rejected);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Running_CanTransitionTo_BudgetExceeded()
    {
        var machine = new TaskStateMachine();
        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.Running);

        machine.TransitionTo(TaskState.BudgetExceeded);

        machine.CurrentState.Should().Be(TaskState.BudgetExceeded);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Running_CanTransitionTo_ScopeViolation()
    {
        var machine = new TaskStateMachine();
        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.Running);

        machine.TransitionTo(TaskState.ScopeViolation);

        machine.CurrentState.Should().Be(TaskState.ScopeViolation);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Running_CanTransitionTo_TimedOut()
    {
        var machine = new TaskStateMachine();
        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.Running);

        machine.TransitionTo(TaskState.TimedOut);

        machine.CurrentState.Should().Be(TaskState.TimedOut);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Running_CanTransitionTo_AgentFailed()
    {
        var machine = new TaskStateMachine();
        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.Running);

        machine.TransitionTo(TaskState.AgentFailed);

        machine.CurrentState.Should().Be(TaskState.AgentFailed);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Verifying_CanTransitionTo_Rejected()
    {
        var machine = new TaskStateMachine();
        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.Running);
        machine.TransitionTo(TaskState.CandidateProduced);
        machine.TransitionTo(TaskState.Verifying);

        machine.TransitionTo(TaskState.Rejected);

        machine.CurrentState.Should().Be(TaskState.Rejected);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Verifying_CanTransitionTo_HumanReviewRequired()
    {
        var machine = new TaskStateMachine();
        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.Running);
        machine.TransitionTo(TaskState.CandidateProduced);
        machine.TransitionTo(TaskState.Verifying);

        machine.TransitionTo(TaskState.HumanReviewRequired);

        machine.CurrentState.Should().Be(TaskState.HumanReviewRequired);
        machine.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void TerminalStates_CannotTransitionToAnything()
    {
        var terminalStates = new[]
        {
            TaskState.Verified,
            TaskState.Rejected,
            TaskState.HumanReviewRequired,
            TaskState.BudgetExceeded,
            TaskState.ScopeViolation,
            TaskState.TimedOut,
            TaskState.AgentFailed,
            TaskState.Cancelled
        };

        foreach (var terminal in terminalStates)
        {
            var machine = new TaskStateMachine();
            // Navigate to terminal state
            switch (terminal)
            {
                case TaskState.Cancelled:
                    machine.TransitionTo(TaskState.Cancelled);
                    break;
                case TaskState.Verified:
                    machine.TransitionTo(TaskState.ContractReady);
                    machine.TransitionTo(TaskState.Planned);
                    machine.TransitionTo(TaskState.Running);
                    machine.TransitionTo(TaskState.CandidateProduced);
                    machine.TransitionTo(TaskState.Verifying);
                    machine.TransitionTo(TaskState.Verified);
                    break;
                case TaskState.Rejected:
                    machine.TransitionTo(TaskState.ContractReady);
                    machine.TransitionTo(TaskState.Planned);
                    machine.TransitionTo(TaskState.Running);
                    machine.TransitionTo(TaskState.CandidateProduced);
                    machine.TransitionTo(TaskState.Verifying);
                    machine.TransitionTo(TaskState.Rejected);
                    break;
                case TaskState.HumanReviewRequired:
                    machine.TransitionTo(TaskState.ContractReady);
                    machine.TransitionTo(TaskState.Planned);
                    machine.TransitionTo(TaskState.Running);
                    machine.TransitionTo(TaskState.CandidateProduced);
                    machine.TransitionTo(TaskState.Verifying);
                    machine.TransitionTo(TaskState.HumanReviewRequired);
                    break;
                case TaskState.BudgetExceeded:
                    machine.TransitionTo(TaskState.ContractReady);
                    machine.TransitionTo(TaskState.Planned);
                    machine.TransitionTo(TaskState.Running);
                    machine.TransitionTo(TaskState.BudgetExceeded);
                    break;
                case TaskState.ScopeViolation:
                    machine.TransitionTo(TaskState.ContractReady);
                    machine.TransitionTo(TaskState.Planned);
                    machine.TransitionTo(TaskState.Running);
                    machine.TransitionTo(TaskState.ScopeViolation);
                    break;
                case TaskState.TimedOut:
                    machine.TransitionTo(TaskState.ContractReady);
                    machine.TransitionTo(TaskState.Planned);
                    machine.TransitionTo(TaskState.Running);
                    machine.TransitionTo(TaskState.TimedOut);
                    break;
                case TaskState.AgentFailed:
                    machine.TransitionTo(TaskState.ContractReady);
                    machine.TransitionTo(TaskState.Planned);
                    machine.TransitionTo(TaskState.Running);
                    machine.TransitionTo(TaskState.AgentFailed);
                    break;
            }

            machine.IsTerminal.Should().BeTrue($"{terminal} should be terminal");
            machine.CanTransitionTo(TaskState.Running).Should().BeFalse($"{terminal} should not allow transitions");
        }
    }

    [Fact]
    public void CanTransitionTo_ReturnsFalse_ForInvalidTransition()
    {
        var machine = new TaskStateMachine();

        machine.CanTransitionTo(TaskState.Verified).Should().BeFalse();
        machine.CanTransitionTo(TaskState.Running).Should().BeFalse();
        machine.CanTransitionTo(TaskState.ContractReady).Should().BeTrue();
    }

    [Fact]
    public void History_RecordsAllTransitions()
    {
        var machine = new TaskStateMachine();

        machine.TransitionTo(TaskState.ContractReady);
        machine.TransitionTo(TaskState.Planned);
        machine.TransitionTo(TaskState.Running);

        machine.History.Should().HaveCount(3);
        machine.History[0].Should().Be((TaskState.Created, TaskState.ContractReady, machine.History[0].At));
        machine.History[1].Should().Be((TaskState.ContractReady, TaskState.Planned, machine.History[1].At));
        machine.History[2].Should().Be((TaskState.Planned, TaskState.Running, machine.History[2].At));
    }
}
