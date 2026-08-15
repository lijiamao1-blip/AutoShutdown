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

/// <summary>
/// 用户对强制冲突仲裁的决策：立即执行指定任务（S13-T09 仲裁消费方）。
/// 其余候选任务由引擎按契约改期 ≥5 分钟（Waiting）或取消（Confirming，白名单无回边）；
/// 无待决仲裁或赢家不在候选中时拒绝。
/// </summary>
public sealed record ResolveArbitrationCommand(Guid WinnerTaskId) : SchedulerCommand;
