namespace AutoShutdown.Core.Scheduling;

public enum NextExecutionStatus
{
    Unknown = 0,
    Success = 1,
    InvalidTaskKind = 2,
    MissingCountdownDuration = 3,
    InvalidCountdownDuration = 4,
    MissingTargetTimeOfDay = 5,
    NoFutureOccurrence = 6,
    InvalidLocalTime = 7
}

public sealed record NextExecutionResult
{
    public NextExecutionStatus Status { get; init; } = NextExecutionStatus.Unknown;

    public DateTimeOffset? ScheduledFireTime { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == NextExecutionStatus.Success;
}
