using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Tasks;

public sealed class TaskService : ITaskService
{
    private const string SourceName = "TaskService";

    private static readonly TimeSpan MaxSnoozeDuration = TimeSpan.FromDays(7);

    private readonly INextExecutionCalculator _nextExecutionCalculator;
    private readonly ITaskInstanceStateMachine _stateMachine;
    private readonly IIdentifierGenerator _identifierGenerator;
    private readonly TaskCollection _tasks = new();

    public TaskService(
        INextExecutionCalculator nextExecutionCalculator,
        ITaskInstanceStateMachine stateMachine,
        IIdentifierGenerator identifierGenerator)
    {
        ArgumentNullException.ThrowIfNull(nextExecutionCalculator);
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(identifierGenerator);

        _nextExecutionCalculator = nextExecutionCalculator;
        _stateMachine = stateMachine;
        _identifierGenerator = identifierGenerator;
        _tasks.Changed += OnTaskCollectionChanged;
    }

    /// <summary>
    /// 任务定义集合变更事件（S22 CP3）：透传 TaskCollection.Changed，供 outbound 同步协调器消费。
    /// </summary>
    public event EventHandler<TaskCollectionChangedEventArgs>? CollectionChanged;

    private void OnTaskCollectionChanged(object? sender, TaskCollectionChangedEventArgs args)
        => CollectionChanged?.Invoke(this, args);

    // ===== 任务定义领域模型集合化 CRUD（S13-T02A，内存领域模型，无持久化） =====

    public TaskCollectionResult Add(TaskDefinition definition) => _tasks.Add(definition);

    public TaskCollectionResult Update(TaskDefinition definition) => _tasks.Update(definition);

    public TaskCollectionResult Remove(Guid taskId) => _tasks.Remove(taskId);

    public TaskCollectionResult SetEnabled(Guid taskId, bool isEnabled) => _tasks.SetEnabled(taskId, isEnabled);

    public TaskDefinition? Get(Guid taskId) => _tasks.Get(taskId);

    public IReadOnlyCollection<TaskDefinition> GetAll() => _tasks.Items;

    public TaskCommandResult Create(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(timeZone);

        var structuralError = TaskDefinitionValidator.GetStructuralError(definition);
        if (structuralError is not null)
        {
            return Failure(TaskCommandStatus.InvalidDefinition, structuralError);
        }

        var schedule = _nextExecutionCalculator.Calculate(definition, now, timeZone);

        // 过期一次性任务不追溯执行：诞生为 Waiting 后按冻结白名单
        // Waiting→Cancelled（OneTimeExpired）终结并告知（S14 契约）。
        if (schedule.Status == NextExecutionStatus.OneTimeExpired)
        {
            var expiredInstanceId = _identifierGenerator.NewId();
            var expiredStageToken = _identifierGenerator.NewId();
            if (expiredInstanceId == Guid.Empty
                || expiredStageToken == Guid.Empty
                || expiredInstanceId == expiredStageToken)
            {
                return Failure(
                    TaskCommandStatus.InvalidCurrentInstance,
                    "The generated identifiers are not valid.");
            }

            var expiredTransition = _stateMachine.TryTransition(
                TaskInstanceState.Waiting,
                TaskInstanceState.Cancelled,
                TaskInstanceStateTransitionCause.OneTimeExpired,
                SourceName);
            if (!expiredTransition.Allowed)
            {
                return new TaskCommandResult
                {
                    Status = TaskCommandStatus.TransitionRejected,
                    TransitionDecisionCode = expiredTransition.DecisionCode,
                    Message = expiredTransition.Reason
                };
            }

            var expiredInstance = new TaskInstance
            {
                InstanceId = expiredInstanceId,
                SourceTaskId = definition.Id,
                ActionSnapshot = definition.Action,
                State = TaskInstanceState.Cancelled,
                ScheduledFireTime = now.ToUniversalTime(),
                WarningStartTime = null,
                StageToken = expiredStageToken,
                HasExecuted = false,
                CreatedAt = now.ToUniversalTime(),
                RealPowerConfirmed = definition.RealPowerConfirmed,
                UseUnattended = definition.UseUnattended,
                TargetMachineId = definition.TargetMachineId,
                RtcWakeTimeUtc = definition.RtcWakeTimeUtc
            };

            return new TaskCommandResult
            {
                Status = TaskCommandStatus.Success,
                Instance = expiredInstance,
                Message = "The one-time task has already expired and was cancelled."
            };
        }

        if (!schedule.Succeeded)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.ScheduleCalculationFailed,
                ScheduleStatus = schedule.Status,
                Message = schedule.Message
            };
        }

        var instanceId = _identifierGenerator.NewId();
        var stageToken = _identifierGenerator.NewId();
        if (instanceId == Guid.Empty || stageToken == Guid.Empty || instanceId == stageToken)
        {
            return Failure(
                TaskCommandStatus.InvalidCurrentInstance,
                "The generated identifiers are not valid.");
        }

        var fireTime = schedule.ScheduledFireTime!.Value;
        var warningStartTime = ComputeWarningStartTime(definition.WarningSeconds, fireTime, now);

        // V2：实例「诞生」于 Waiting 态；无需从 Idle 的状态机转换（V2 无 Idle）。
        var instance = new TaskInstance
        {
            InstanceId = instanceId,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action,
            State = TaskInstanceState.Waiting,
            ScheduledFireTime = fireTime,
            WarningStartTime = warningStartTime,
            StageToken = stageToken,
            HasExecuted = false,
            CreatedAt = now.ToUniversalTime(),
            RealPowerConfirmed = definition.RealPowerConfirmed,
            UseUnattended = definition.UseUnattended,
            TargetMachineId = definition.TargetMachineId,
            RtcWakeTimeUtc = definition.RtcWakeTimeUtc
        };

        return new TaskCommandResult
        {
            Status = TaskCommandStatus.Success,
            Instance = instance,
            Message = "The task instance was created."
        };
    }

    public TaskCommandResult Snooze(
        TaskInstance current,
        TimeSpan duration,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.HasExecuted)
        {
            return Failure(TaskCommandStatus.AlreadyExecuted, "An executed instance cannot be snoozed.");
        }

        if (duration <= TimeSpan.Zero || duration > MaxSnoozeDuration)
        {
            return Failure(
                TaskCommandStatus.InvalidDuration,
                "Snooze duration must be greater than zero and at most 7 days.");
        }

        if (current.State != TaskInstanceState.Waiting)
        {
            // V2 白名单无 confirming→waiting 边：确认/终态实例不可 snooze，仅 Waiting 可顺延。
            var rejected = _stateMachine.TryTransition(
                current.State,
                TaskInstanceState.Waiting,
                TaskInstanceStateTransitionCause.Reschedule,
                SourceName);
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = rejected.DecisionCode,
                Message = "Only Waiting instances can be snoozed: " + rejected.Reason
            };
        }

        var stageToken = _identifierGenerator.NewId();
        if (!IsDistinctStageToken(stageToken, current))
        {
            return Failure(
                TaskCommandStatus.InvalidCurrentInstance,
                "The generated stage token is not valid.");
        }

        // Waiting → Waiting：仅顺延触发时间并刷新 StageToken（字段级重排，非状态转换）。
        var snoozed = current with
        {
            ScheduledFireTime = now.ToUniversalTime().Add(duration),
            WarningStartTime = null,
            StageToken = stageToken
        };

        return new TaskCommandResult
        {
            Status = TaskCommandStatus.Success,
            Instance = snoozed,
            Message = "The task instance was snoozed."
        };
    }

    public TaskCommandResult Cancel(TaskInstance current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.HasExecuted)
        {
            return Failure(TaskCommandStatus.AlreadyExecuted, "An executed instance cannot be cancelled.");
        }

        var cause = current.State == TaskInstanceState.Confirming
            ? TaskInstanceStateTransitionCause.CancelledDuringConfirmation
            : TaskInstanceStateTransitionCause.CancelByUser;

        var transition = _stateMachine.TryTransition(
            current.State,
            TaskInstanceState.Cancelled,
            cause,
            SourceName);
        if (!transition.Allowed)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = transition.DecisionCode,
                Message = transition.Reason
            };
        }

        var stageToken = _identifierGenerator.NewId();
        if (!IsDistinctStageToken(stageToken, current))
        {
            return Failure(
                TaskCommandStatus.InvalidCurrentInstance,
                "The generated stage token is not valid.");
        }

        var cancelled = current with
        {
            State = TaskInstanceState.Cancelled,
            WarningStartTime = null,
            StageToken = stageToken
        };

        return new TaskCommandResult
        {
            Status = TaskCommandStatus.Success,
            Instance = cancelled,
            Message = "The task instance was cancelled."
        };
    }

    public TaskCommandResult RescheduleAfterArbitration(
        TaskInstance current,
        TimeSpan delay,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (delay < TaskArbitrator.MinimumRescheduleDelay)
        {
            return Failure(
                TaskCommandStatus.InvalidDuration,
                "Arbitration reschedule delay must be at least 5 minutes.");
        }

        if (current.State != TaskInstanceState.Waiting)
        {
            // 落选改期仅在 Waiting 成立：Confirming 已进提醒窗口，冻结白名单无回边，
            // 无法字段级改期，由引擎按取消承载（防双重电源执行），此处结构拒绝。
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                Message = "Only Waiting instances can be rescheduled after arbitration."
            };
        }

        var stageToken = _identifierGenerator.NewId();
        if (!IsDistinctStageToken(stageToken, current))
        {
            return Failure(
                TaskCommandStatus.InvalidCurrentInstance,
                "The generated stage token is not valid.");
        }

        // Waiting → Waiting：字段级重排（非状态转换），刷新 StageToken 并清除告警窗口。
        var rescheduled = current with
        {
            State = TaskInstanceState.Waiting,
            ScheduledFireTime = now.ToUniversalTime().Add(delay),
            WarningStartTime = null,
            StageToken = stageToken,
            HasExecuted = false
        };

        return new TaskCommandResult
        {
            Status = TaskCommandStatus.Success,
            Instance = rescheduled,
            Message = "The task instance was rescheduled after arbitration."
        };
    }

    public TaskCommandResult RescheduleDaily(
        TaskDefinition definition,
        TaskInstance current,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (definition.Kind != TaskKind.DailyAt)
        {
            return Failure(
                TaskCommandStatus.InvalidDefinition,
                "RescheduleDaily only supports DailyAt tasks.");
        }

        if (definition.Id != current.SourceTaskId || definition.Action != current.ActionSnapshot)
        {
            return Failure(
                TaskCommandStatus.InvalidDefinition,
                "The definition identity or action does not match the current instance.");
        }

        // V2：executed → waiting（Reschedule，刷新 StageToken）。仅 Executed 可重排。
        var transition = _stateMachine.TryTransition(
            current.State,
            TaskInstanceState.Waiting,
            TaskInstanceStateTransitionCause.Reschedule,
            SourceName);
        if (!transition.Allowed)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = transition.DecisionCode,
                Message = transition.Reason
            };
        }

        var schedule = _nextExecutionCalculator.Calculate(definition, now, timeZone);
        if (!schedule.Succeeded)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.ScheduleCalculationFailed,
                ScheduleStatus = schedule.Status,
                Message = schedule.Message
            };
        }

        var stageToken = _identifierGenerator.NewId();
        if (!IsDistinctStageToken(stageToken, current))
        {
            return Failure(
                TaskCommandStatus.InvalidCurrentInstance,
                "The generated stage token is not valid.");
        }

        var fireTime = schedule.ScheduledFireTime!.Value;
        var warningStartTime = ComputeWarningStartTime(definition.WarningSeconds, fireTime, now);

        var rescheduled = current with
        {
            State = TaskInstanceState.Waiting,
            ScheduledFireTime = fireTime,
            WarningStartTime = warningStartTime,
            StageToken = stageToken,
            HasExecuted = false
        };

        return new TaskCommandResult
        {
            Status = TaskCommandStatus.Success,
            Instance = rescheduled,
            Message = "The daily task was rescheduled."
        };
    }

    public TaskCommandResult RescheduleRecurring(
        TaskDefinition definition,
        TaskInstance current,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (!TaskDefinitionValidator.IsRecurringKind(definition.Kind))
        {
            return Failure(
                TaskCommandStatus.InvalidDefinition,
                "RescheduleRecurring only supports recurring tasks (DailyAt, Weekdays, NthWorkdayOfMonth).");
        }

        if (definition.Id != current.SourceTaskId || definition.Action != current.ActionSnapshot)
        {
            return Failure(
                TaskCommandStatus.InvalidDefinition,
                "The definition identity or action does not match the current instance.");
        }

        // 周期规则：executed → waiting（Reschedule，刷新 StageToken）。仅 Executed 可重排。
        var transition = _stateMachine.TryTransition(
            current.State,
            TaskInstanceState.Waiting,
            TaskInstanceStateTransitionCause.Reschedule,
            SourceName);
        if (!transition.Allowed)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = transition.DecisionCode,
                Message = transition.Reason
            };
        }

        var schedule = _nextExecutionCalculator.Calculate(definition, now, timeZone);
        if (!schedule.Succeeded)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.ScheduleCalculationFailed,
                ScheduleStatus = schedule.Status,
                Message = schedule.Message
            };
        }

        var stageToken = _identifierGenerator.NewId();
        if (!IsDistinctStageToken(stageToken, current))
        {
            return Failure(
                TaskCommandStatus.InvalidCurrentInstance,
                "The generated stage token is not valid.");
        }

        var fireTime = schedule.ScheduledFireTime!.Value;
        var warningStartTime = ComputeWarningStartTime(definition.WarningSeconds, fireTime, now);

        var rescheduled = current with
        {
            State = TaskInstanceState.Waiting,
            ScheduledFireTime = fireTime,
            WarningStartTime = warningStartTime,
            StageToken = stageToken,
            HasExecuted = false
        };

        return new TaskCommandResult
        {
            Status = TaskCommandStatus.Success,
            Instance = rescheduled,
            Message = "The recurring task was rescheduled."
        };
    }

    private static DateTimeOffset? ComputeWarningStartTime(
        int? warningSeconds,
        DateTimeOffset fireTime,
        DateTimeOffset now)
    {
        if (warningSeconds is not > 0)
        {
            return null;
        }

        var candidate = fireTime.AddSeconds(-warningSeconds.Value);
        var clamped = candidate < now ? now : candidate;
        return clamped.ToUniversalTime();
    }

    private static bool IsDistinctStageToken(Guid stageToken, TaskInstance current)
    {
        return stageToken != Guid.Empty
            && stageToken != current.StageToken
            && stageToken != current.InstanceId;
    }

    private static TaskCommandResult Failure(
        TaskCommandStatus status,
        string message) => new()
        {
            Status = status,
            Message = message
        };
}
