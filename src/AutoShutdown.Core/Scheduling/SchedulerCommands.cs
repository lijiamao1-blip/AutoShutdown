using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling;

public abstract record SchedulerCommand;

public sealed record CreateTaskCommand(TaskDefinition Definition) : SchedulerCommand;

public sealed record SnoozeTaskCommand(
    Guid ExpectedInstanceId,
    Guid ExpectedStageToken,
    TimeSpan Duration) : SchedulerCommand;

public sealed record CancelTaskCommand(
    Guid ExpectedInstanceId,
    Guid ExpectedStageToken) : SchedulerCommand;

public sealed record ClearTerminalTaskCommand(Guid ExpectedInstanceId) : SchedulerCommand;

/// <summary>启用/禁用任务（按任务定义 id 定位；无实例阶段令牌）。</summary>
public sealed record SetTaskEnabledCommand(Guid TaskId, bool IsEnabled) : SchedulerCommand;
