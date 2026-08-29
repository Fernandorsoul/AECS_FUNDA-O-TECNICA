namespace AECS.Domain.Exceptions;

public class InvalidStateTransitionException : Exception
{
    public string CurrentState { get; }
    public string TargetState { get; }

    public InvalidStateTransitionException(string currentState, string targetState)
        : base($"Invalid state transition from '{currentState}' to '{targetState}'.")
    {
        CurrentState = currentState;
        TargetState = targetState;
    }
}
