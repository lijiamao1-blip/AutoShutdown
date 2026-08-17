using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

public enum ExternalTriggerOutcomeStatus
{
    Unknown = 0,

    /// <summary>已回交本地唯一 Workflow 并成功执行。</summary>
    Success = 1,

    /// <summary>本地任务 id 已不存在（外部任务陈旧）：不执行任何电源。</summary>
    TaskNotFound = 2,

    /// <summary>本地任务已禁用：不执行。</summary>
    Disabled = 3,

    /// <summary>时间闸门拒绝（偏离本地调度窗口/无未来触发点）：不执行，本地调度器为事实源。</summary>
    GateRejected = 4,

    /// <summary>tasks.json 缺失/损坏/非法/版本过高：fail-closed，绝不执行电源。</summary>
    ConfigLoadFailed = 5,

    /// <summary>本地 Workflow/handler 拒绝执行或抛异常：不执行电源。</summary>
    ExecutionFailed = 6
}

public sealed record ExternalTriggerOutcome
{
    public ExternalTriggerOutcomeStatus Status { get; init; } = ExternalTriggerOutcomeStatus.Unknown;

    public string? Message { get; init; }
}

/// <summary>
/// 外部触发回调服务（S22 CP4）：外部任务只回调本地应用（--trigger-task &lt;id&gt;），本服务把
/// 回调翻译回本地唯一调度/Workflow。绝不直接执行电源命令——只构造 Executing 实例并交给
/// <see cref="IScheduledTaskHandler"/>（WakeOnLan→执行器，其余→ShutdownWorkflow），
/// ShutdownWorkflow 仍是唯一调用 IPowerService 的模块，双闸门与 Pre-Pipeline 原样生效。
/// 读取 tasks.json 严格区分 NotFound/Corrupt/Invalid/UnsupportedVersion，任何失败 fail-closed。
/// </summary>
public sealed class ExternalTaskTriggerService
{
    private readonly TasksDocumentStore _documentStore;
    private readonly IScheduledTaskHandler _handler;
    private readonly IClock _clock;
    private readonly ExternalTriggerScheduleGate _gate;
    private readonly IIdentifierGenerator _idGenerator;

    public ExternalTaskTriggerService(
        TasksDocumentStore documentStore,
        IScheduledTaskHandler handler,
        IClock clock,
        ExternalTriggerScheduleGate gate,
        IIdentifierGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(idGenerator);

        _documentStore = documentStore;
        _handler = handler;
        _clock = clock;
        _gate = gate;
        _idGenerator = idGenerator;
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

        var instance = BuildExecutingInstance(definition, now);
        try
        {
            await _handler.HandleDueAsync(instance, cancellationToken).ConfigureAwait(false);
            return Outcome(
                ExternalTriggerOutcomeStatus.Success,
                "External trigger routed to the local workflow.");
        }
        catch (Exception exception)
        {
            return Outcome(
                ExternalTriggerOutcomeStatus.ExecutionFailed,
                "The local workflow rejected execution: " + exception.Message);
        }
    }

    /// <summary>
    /// 构造 Executing 实例：直接以 Executing + HasExecuted 进入，满足 ShutdownWorkflow 的
    /// 前置校验；双闸门（RealPowerEnabled + RealPowerConfirmed/无人值守等效确认）在 Workflow
    /// 内裁决，本服务不做任何电源决策。
    /// </summary>
    private TaskInstance BuildExecutingInstance(TaskDefinition definition, DateTimeOffset now)
    {
        var instanceId = _idGenerator.NewId();
        var stageToken = _idGenerator.NewId();
        var utcNow = now.ToUniversalTime();
        return new TaskInstance
        {
            InstanceId = instanceId,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action,
            State = TaskInstanceState.Executing,
            ScheduledFireTime = utcNow,
            WarningStartTime = null,
            StageToken = stageToken,
            HasExecuted = true,
            CreatedAt = utcNow,
            RealPowerConfirmed = definition.RealPowerConfirmed,
            UseUnattended = definition.UseUnattended,
            TargetMachineId = definition.TargetMachineId,
            RtcWakeTimeUtc = definition.RtcWakeTimeUtc
        };
    }

    private static ExternalTriggerOutcome Outcome(
        ExternalTriggerOutcomeStatus status,
        string message) => new()
        {
            Status = status,
            Message = message
        };
}
