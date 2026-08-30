using AECS.Domain.Enums;
using AECS.Domain.Exceptions;

namespace AECS.Domain.Models;

public class TaskStateMachine
{
    private static readonly Dictionary<TaskState, HashSet<TaskState>> ValidTransitions = new()
    {
        [TaskState.Created] = [TaskState.ContractReady, TaskState.Cancelled],
        [TaskState.ContractReady] = [TaskState.Planned, TaskState.Cancelled],
        [TaskState.Planned] = [TaskState.Running, TaskState.Cancelled],
        [TaskState.Running] =
        [
            TaskState.CandidateProduced,
            TaskState.BudgetExceeded,
            TaskState.ScopeViolation,
            TaskState.TimedOut,
            TaskState.AgentFailed,
            TaskState.Cancelled
        ],
        [TaskState.CandidateProduced] =
        [
            TaskState.Verifying,
            TaskState.Rejected,
            TaskState.Cancelled
        ],
        [TaskState.Verifying] =
        [
            TaskState.Verified,
            TaskState.Rejected,
            TaskState.HumanReviewRequired
        ],
        // Terminal states — no transitions out
        [TaskState.Verified] = [],
        [TaskState.Rejected] = [],
        [TaskState.HumanReviewRequired] = [],
        [TaskState.BudgetExceeded] = [],
        [TaskState.ScopeViolation] = [],
        [TaskState.TimedOut] = [],
        [TaskState.AgentFailed] = [],
        [TaskState.Cancelled] = []
    };

    public TaskState CurrentState { get; private set; } = TaskState.Created;
    public List<(TaskState From, TaskState To, DateTime At)> History { get; } = [];

    public void TransitionTo(TaskState target)
    {
        if (!ValidTransitions.TryGetValue(CurrentState, out var allowed))
            throw new InvalidStateTransitionException(CurrentState.ToString(), target.ToString());

        if (!allowed.Contains(target))
            throw new InvalidStateTransitionException(CurrentState.ToString(), target.ToString());

        History.Add((CurrentState, target, DateTime.UtcNow));
        CurrentState = target;
    }

    public bool CanTransitionTo(TaskState target)
    {
        return ValidTransitions.TryGetValue(CurrentState, out var allowed) && allowed.Contains(target);
    }

    public bool IsTerminal => ValidTransitions.TryGetValue(CurrentState, out var allowed) && allowed.Count == 0;
}
