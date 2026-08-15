using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Tasks;

public sealed class TaskService : ITaskService
{
    private static readonly TimeSpan MaxSnoozeDuration = TimeSpan.FromDays(7);

    private readonly INextExecutionCalculator _nextExecutionCalculator;
    private readonly ITaskStateMachine _stateMachine;
    private readonly IIdentifierGenerator _identifierGenerator;
    private readonly TaskCollection _tasks = new();

    public TaskService(
        INextExecutionCalculator nextExecutionCalculator,
        ITaskStateMachine stateMachine,
        IIdentifierGenerator identifierGenerator)
    {
        ArgumentNullException.ThrowIfNull(nextExecutionCalculator);
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(identifierGenerator);

        _nextExecutionCalculator = nextExecutionCalculator;
        _stateMachine = stateMachine;
        _identifierGenerator = identifierGenerator;
    }

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
        if (!schedule.Succeeded)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.ScheduleCalculationFailed,
                ScheduleStatus = schedule.Status,
                Message = schedule.Message
            };
        }

        var transition = _stateMachine.TryTransition(
            TaskState.Idle,
            TaskState.Scheduled,
            TaskTransitionCause.Schedule);
        if (!transition.Allowed)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = transition.DecisionCode,
                Message = transition.Message
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

        var instance = new TaskInstance
        {
            InstanceId = instanceId,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action,
            State = TaskState.Scheduled,
            ScheduledFireTime = fireTime,
            WarningStartTime = warningStartTime,
            StageToken = stageToken,
            HasExecuted = false,
            CreatedAt = now.ToUniversalTime(),
            RealPowerConfirmed = definition.RealPowerConfirmed
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

        var cause = current.State switch
        {
            TaskState.Warning => TaskTransitionCause.SnoozeByUser,
            _ => TaskTransitionCause.Reschedule
        };

        var transition = _stateMachine.TryTransition(current.State, TaskState.Scheduled, cause);
        if (!transition.Allowed)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = transition.DecisionCode,
                Message = transition.Message
            };
        }

        var stageToken = _identifierGenerator.NewId();
        if (!IsDistinctStageToken(stageToken, current))
        {
            return Failure(
                TaskCommandStatus.InvalidCurrentInstance,
                "The generated stage token is not valid.");
        }

        var snoozed = current with
        {
            State = TaskState.Scheduled,
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

        var transition = _stateMachine.TryTransition(
            current.State,
            TaskState.Cancelled,
            TaskTransitionCause.CancelByUser);
        if (!transition.Allowed)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = transition.DecisionCode,
                Message = transition.Message
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
            State = TaskState.Cancelled,
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

        if (current.HasExecuted)
        {
            return Failure(
                TaskCommandStatus.AlreadyExecuted,
                "An executed instance cannot be rescheduled.");
        }

        if (current.State != TaskState.Idle)
        {
            var rejected = _stateMachine.TryTransition(
                current.State,
                TaskState.Scheduled,
                TaskTransitionCause.Schedule);

            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = rejected.DecisionCode,
                Message = rejected.Message
            };
        }

        var transition = _stateMachine.TryTransition(
            TaskState.Idle,
            TaskState.Scheduled,
            TaskTransitionCause.Schedule);
        if (!transition.Allowed)
        {
            return new TaskCommandResult
            {
                Status = TaskCommandStatus.TransitionRejected,
                TransitionDecisionCode = transition.DecisionCode,
                Message = transition.Message
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
            State = TaskState.Scheduled,
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
