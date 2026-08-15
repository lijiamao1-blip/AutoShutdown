using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Tasks;

public enum TaskCommandStatus
{
    Unknown = 0,
    Success = 1,
    InvalidDefinition = 2,
    ScheduleCalculationFailed = 3,
    InvalidCurrentInstance = 4,
    InvalidDuration = 5,
    TransitionRejected = 6,
    AlreadyExecuted = 7
}

public sealed record TaskCommandResult
{
    public TaskCommandStatus Status { get; init; } = TaskCommandStatus.Unknown;

    public TaskInstance? Instance { get; init; }

    public NextExecutionStatus? ScheduleStatus { get; init; }

    public TaskInstanceStateTransitionDecisionCode? TransitionDecisionCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == TaskCommandStatus.Success;
}
