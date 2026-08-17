using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

public enum ExternalTriggerOutcomeStatus
{
    Unknown = 0,

    /// <summary>已经本地调度引擎唯一接入/仲裁路径执行成功。</summary>
    Success = 1,

    /// <summary>本地任务 id 已不存在（外部任务陈旧）：不执行任何电源。</summary>
    TaskNotFound = 2,

    /// <summary>本地任务已禁用：不执行。</summary>
    Disabled = 3,

    /// <summary>时间闸门拒绝（偏离本地调度窗口/无未来触发点）：不执行，本地调度器为事实源。</summary>
    GateRejected = 4,

    /// <summary>tasks.json 缺失/损坏/非法/版本过高：fail-closed，绝不执行电源。</summary>
    ConfigLoadFailed = 5,

    /// <summary>本地调度引擎/倒计时边界裁决拒绝执行或抛异常：不执行电源。</summary>
    ExecutionFailed = 6,

    /// <summary>并发去重：本地调度器已拥有同一任务同一触发窗口，外部触发冗余，不重复执行。</summary>
    Deduped = 7
}

public sealed record ExternalTriggerOutcome
{
    public ExternalTriggerOutcomeStatus Status { get; init; } = ExternalTriggerOutcomeStatus.Unknown;

    public string? Message { get; init; }
}

/// <summary>
/// 外部触发回调服务（S22 CP4 + D2）：外部任务只回调本地应用（--trigger-task &lt;id&gt;），本服务
/// 只读本地事实源做 fail-closed 前置校验（配置分类/任务存在/启用/时间闸门），随后把触发经
/// <see cref="ExternalTriggerTaskCommand"/> 提交进 <see cref="ISchedulerEngine"/> 的唯一接入/
/// 仲裁路径。引擎按同一任务单实例（并发去重）+ 倒计时边界裁决（S20-D1 无人值守授权、
/// S20-D2 Idle 输入恢复/未知/监视器缺失、人工确认）决定是否执行，并只经唯一 handler/
/// ShutdownWorkflow 调用电源；本服务绝不直接调用 handler 或 Workflow，也绝不直接执行电源命令。
/// 读取 tasks.json 严格区分 NotFound/Corrupt/Invalid/UnsupportedVersion，任何失败 fail-closed。
/// </summary>
public sealed class ExternalTaskTriggerService
{
    private readonly TasksDocumentStore _documentStore;
    private readonly ISchedulerEngine _engine;
    private readonly IClock _clock;
    private readonly ExternalTriggerScheduleGate _gate;

    public ExternalTaskTriggerService(
        TasksDocumentStore documentStore,
        ISchedulerEngine engine,
        IClock clock,
        ExternalTriggerScheduleGate gate)
    {
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(gate);

        _documentStore = documentStore;
        _engine = engine;
        _clock = clock;
        _gate = gate;
    }

    public async Task<ExternalTriggerOutcome> HandleExternalTriggerAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        if (taskId == Guid.Empty)
        {
            return Outcome(
                ExternalTriggerOutcomeStatus.TaskNotFound,
                "Empty local task id; no power action.");
        }

        // 严格读取本地事实源；任何异常一律 fail-closed。
        var load = await _documentStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        switch (load.Status)
        {
            case TasksLoadStatus.Success:
            case TasksLoadStatus.Migrated:
                break;
            case TasksLoadStatus.NotFound:
                return Outcome(
                    ExternalTriggerOutcomeStatus.ConfigLoadFailed,
                    "The tasks file does not exist; no power action.");
            default:
                return Outcome(
                    ExternalTriggerOutcomeStatus.ConfigLoadFailed,
                    "The tasks file is unavailable (" + load.Status + "): fail-closed, no power action.");
        }

        var definition = load.Document!.Tasks.FirstOrDefault(task => task.Id == taskId);
        if (definition is null)
        {
            return Outcome(
                ExternalTriggerOutcomeStatus.TaskNotFound,
                "Local task " + taskId.ToString("D") + " no longer exists; stale external task ignored.");
        }

        if (!definition.IsEnabled)
        {
            return Outcome(
                ExternalTriggerOutcomeStatus.Disabled,
                "Local task is disabled; no power action.");
        }

        var now = _clock.UtcNow;
        var gate = _gate.Evaluate(definition, now, _clock.LocalTimeZone);
        if (gate.Verdict != ExternalTriggerGateVerdict.Allowed)
        {
            return Outcome(
                ExternalTriggerOutcomeStatus.GateRejected,
                "External trigger outside local schedule window: " + gate.Verdict
                + (string.IsNullOrEmpty(gate.Reason) ? string.Empty : " — " + gate.Reason));
        }

        // S22-D2：外部触发经本地调度引擎唯一接入/仲裁路径执行。引擎负责并发去重、
        // S20-D1/S20-D2 倒计时边界裁决、唯一 handler 调用与唯一电源出口。
        SchedulerCommandResult result;
        try
        {
            result = await _engine.SubmitAsync(
                new ExternalTriggerTaskCommand(taskId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Outcome(
                ExternalTriggerOutcomeStatus.ExecutionFailed,
                "The scheduler engine rejected the external trigger: " + exception.Message);
        }

        return MapEngineResult(result);
    }

    /// <summary>把引擎命令结果映射为外部触发结果（fail-closed：除 Success/Deduped 外一律不执行电源）。</summary>
    private static ExternalTriggerOutcome MapEngineResult(SchedulerCommandResult result)
    {
        switch (result.Status)
        {
            case SchedulerCommandStatus.Success:
                return Outcome(
                    ExternalTriggerOutcomeStatus.Success,
                    "External trigger routed through the local scheduler engine and executed.");

            case SchedulerCommandStatus.ActiveTaskExists:
                return Outcome(
                    ExternalTriggerOutcomeStatus.Deduped,
                    "External trigger deduplicated: the local scheduler already owns this firing window; no duplicate pipeline or power call.");

            case SchedulerCommandStatus.NoCurrentTask:
                return Outcome(
                    ExternalTriggerOutcomeStatus.TaskNotFound,
                    "Local task no longer exists; stale external task ignored.");

            case SchedulerCommandStatus.InvalidCommand:
                return Outcome(
                    ExternalTriggerOutcomeStatus.Disabled,
                    "Local task is disabled; no power action.");

            case SchedulerCommandStatus.NotRunning:
                return Outcome(
                    ExternalTriggerOutcomeStatus.ExecutionFailed,
                    "The scheduler engine is not running; no power action.");

            default:
                return Outcome(
                    ExternalTriggerOutcomeStatus.ExecutionFailed,
                    "The scheduler engine rejected the external trigger: " + result.Message);
        }
    }

    private static ExternalTriggerOutcome Outcome(
        ExternalTriggerOutcomeStatus status,
        string message) => new()
        {
            Status = status,
            Message = message
        };
}
