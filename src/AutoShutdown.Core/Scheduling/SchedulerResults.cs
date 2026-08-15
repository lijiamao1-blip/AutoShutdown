using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling;

public enum SchedulerEngineStatus
{
    Unknown = 0,
    Created = 1,
    Running = 2,
    Faulted = 3,
    Stopped = 4
}

public enum SchedulerCommandStatus
{
    Unknown = 0,
    Success = 1,
    NotRunning = 2,
    Faulted = 3,
    NoCurrentTask = 4,
    ActiveTaskExists = 5,
    StaleCommand = 6,
    TaskServiceRejected = 7,
    PersistenceFailed = 8,
    InvalidCommand = 9,
    TransitionRejected = 10
}

public sealed record SchedulerSnapshot
{
    public static SchedulerSnapshot Empty { get; } = new();

    public SchedulerEngineStatus EngineStatus { get; init; } = SchedulerEngineStatus.Unknown;

    public TaskInstance? CurrentInstance { get; init; }

    public DateTimeOffset LastUpdatedAt { get; init; }

    public string? FaultMessage { get; init; }
}

public sealed record SchedulerCommandResult
{
    public SchedulerCommandStatus Status { get; init; } = SchedulerCommandStatus.Unknown;

    public TaskCommandStatus? TaskCommandStatus { get; init; }

    public TaskTransitionDecisionCode? TransitionDecisionCode { get; init; }

    public SchedulerSnapshot Snapshot { get; init; } = SchedulerSnapshot.Empty;

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == SchedulerCommandStatus.Success;
}
