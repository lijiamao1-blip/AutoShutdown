namespace AutoShutdown.Core.State;

public enum TaskTransitionDecisionCode
{
    Allowed = 0,
    UnknownCurrentState = 1,
    UnknownTargetState = 2,
    UnknownCause = 3,
    TransitionNotAllowed = 4,
    CauseMismatch = 5
}

public sealed record TaskTransitionResult
{
    public bool Allowed { get; init; }

    public TaskState CurrentState { get; init; }

    public TaskState TargetState { get; init; }

    public TaskTransitionCause Cause { get; init; }

    public TaskTransitionDecisionCode DecisionCode { get; init; }

    public string Message { get; init; } = string.Empty;
}
