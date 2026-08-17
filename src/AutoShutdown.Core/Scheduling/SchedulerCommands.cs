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

/// <summary>
/// 外部触发回调（S22-D2）：Windows Task Scheduler 的 --trigger-task &lt;id&gt; 回调经此命令
/// 进入本地调度引擎的唯一接入/仲裁路径。只携带稳定本地 task id，不携带任何电源命令。
/// 引擎按同一任务单实例（并发去重）+ 倒计时边界裁决（S20-D1/S20-D2/人工确认）决定是否
/// 执行，并只经唯一 handler/ShutdownWorkflow 调用电源——绝不重复进入 Pipeline/电源。
/// </summary>
public sealed record ExternalTriggerTaskCommand(Guid TaskId) : SchedulerCommand;

/// <summary>
/// 局域网远程触发（S23 CP3/CP4）：携带稳定本地任务 id + 「是否允许无人值守等效确认」。
/// 只允许经 <see cref="Remote.RemoteRequestHandler"/> 提交：EquivalentUnattendedAllowed 仅当
/// 连接确为 TLS 且白名单已启用 triggerShutdown 时才为 true；引擎据此与本地任务
/// UseUnattended 共同裁决。引擎绝不在无人值守任务上做「无等效的本地倒计时回退」——
/// 无人值守 + 无等效 → 一律拒绝（fail-closed），绝不绕过无人值守策略。
/// 仍走唯一 handler/Workflow 双闸门，绝不引入第二电源出口。
/// </summary>
public sealed record RemoteTriggerTaskCommand(
    Guid TaskId,
    bool EquivalentUnattendedAllowed) : SchedulerCommand;

/// <summary>
/// 局域网远程取消（S23 CP4）：按本地任务 id 定位。仅允许取消 Confirming（倒计时/待决）实例；
/// Waiting/Running/Executing/终态一律拒绝——远程不得替用户撤销已排定的任务或正在执行的动作。
/// </summary>
public sealed record RemoteCancelTaskCommand(Guid TaskId) : SchedulerCommand;
