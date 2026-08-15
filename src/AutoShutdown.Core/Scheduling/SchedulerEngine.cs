using System.Threading.Channels;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Workflow;

namespace AutoShutdown.Core.Scheduling;

public sealed class SchedulerEngine : ISchedulerEngine
{
    private const string SourceName = "SchedulerEngine";

    private readonly Channel<PendingCommand> _channel;
    private readonly IClock _clock;
    private readonly IAsyncDeadline _deadline;
    private readonly ITaskService _taskService;
    private readonly ITaskInstanceStateMachine _stateMachine;
    private readonly IIdentifierGenerator _identifierGenerator;
    private readonly IScheduledTaskHandler _handler;
    private readonly ITaskArbitrator _arbitrator;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly object _sync = new();

    private SchedulerSnapshot _snapshot = SchedulerSnapshot.Empty;
    private Task? _runTask;

    public SchedulerEngine(
        IStorage storage,
        IClock clock,
        IAsyncDeadline deadline,
        ITaskService taskService,
        ITaskInstanceStateMachine stateMachine,
        IIdentifierGenerator identifierGenerator,
        IScheduledTaskHandler handler,
        ITaskArbitrator arbitrator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(deadline);
        ArgumentNullException.ThrowIfNull(taskService);
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(identifierGenerator);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(arbitrator);

        _clock = clock;
        _deadline = deadline;
        _taskService = taskService;
        _stateMachine = stateMachine;
        _identifierGenerator = identifierGenerator;
        _handler = handler;
        _arbitrator = arbitrator;
        _runtimeStateStore = new RuntimeStateStore(storage);

        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateUnbounded<PendingCommand>(options);
        _snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Created };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task loop;
        lock (_sync)
        {
            if (_runTask is not null)
            {
                throw new InvalidOperationException("The scheduler engine can only run once.");
            }

            _runTask = RunLoopAsync(cancellationToken);
            loop = _runTask;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            await ShutdownPendingCommandsAsync().ConfigureAwait(false);
        }
    }

    public Task<SchedulerCommandResult> SubmitAsync(
        SchedulerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var snapshot = GetSnapshot();
        switch (snapshot.EngineStatus)
        {
            case SchedulerEngineStatus.Created:
            case SchedulerEngineStatus.Stopped:
                return Task.FromResult(Rejected(
                    snapshot,
                    SchedulerCommandStatus.NotRunning,
                    "The engine is not running."));
            case SchedulerEngineStatus.Faulted:
                return Task.FromResult(Rejected(
                    snapshot,
                    SchedulerCommandStatus.Faulted,
                    "The engine is faulted: " + (snapshot.FaultMessage ?? "unknown failure.")));
            case SchedulerEngineStatus.Running:
                break;
            default:
                return Task.FromResult(Rejected(
                    snapshot,
                    SchedulerCommandStatus.NotRunning,
                    "The engine is not running."));
        }

        var tcs = new TaskCompletionSource<SchedulerCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_channel.Writer.TryWrite(new PendingCommand(command, tcs)))
        {
            return Task.FromResult(Rejected(
                snapshot,
                SchedulerCommandStatus.Faulted,
                "The command channel is closed."));
        }

        return tcs.Task;
    }

    public SchedulerSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        if (!await TryRestoreAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var snapshot = GetSnapshot();
            if (snapshot.EngineStatus != SchedulerEngineStatus.Running)
            {
                return;
            }

            if (await TryProcessDueAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var now = _clock.UtcNow.ToUniversalTime();
            var nextDeadline = ComputeNextDeadline(now);

            using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readTask = _channel.Reader.ReadAsync(iterationCts.Token).AsTask();
            Task? deadlineTask = nextDeadline is { } deadline
                ? _deadline.WaitUntilAsync(deadline, iterationCts.Token)
                : null;

            if (deadlineTask is null)
            {
                var pending = await readTask.ConfigureAwait(false);
                await ProcessCommandAsync(pending, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var completed = await Task.WhenAny(readTask, deadlineTask).ConfigureAwait(false);

            if (ReferenceEquals(completed, readTask) || readTask.IsCompleted)
            {
                // Commands win over deadlines, even when both complete together.
                iterationCts.Cancel();
                await SuppressAsync(deadlineTask).ConfigureAwait(false);
                var pending = await readTask.ConfigureAwait(false);
                await ProcessCommandAsync(pending, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                iterationCts.Cancel();
                await SuppressAsync(readTask).ConfigureAwait(false);
                // Deadline fired: loop back to re-derive due instances from fresh state.
            }
        }
    }

    private async Task<bool> TryRestoreAsync(CancellationToken cancellationToken)
    {
        // 崩溃恢复已迁出至 CrashRecoveryManager（启动链在调度循环前执行）；
        // 引擎此处只载入（已恢复的）运行态并建立初始快照，不再做瞬态实例中断。
        var load = await _runtimeStateStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        switch (load.Status)
        {
            case RuntimeStateLoadStatus.NotFound:
                lock (_sync)
                {
                    _snapshot = new SchedulerSnapshot
                    {
                        EngineStatus = SchedulerEngineStatus.Running,
                        Instances = new Dictionary<Guid, TaskInstance>(),
                        LastUpdatedAt = _clock.UtcNow.ToUniversalTime()
                    };
                }

                return true;
            case RuntimeStateLoadStatus.Success:
            case RuntimeStateLoadStatus.Migrated:
                lock (_sync)
                {
                    _snapshot = new SchedulerSnapshot
                    {
                        EngineStatus = SchedulerEngineStatus.Running,
                        Instances = load.State!.Instances,
                        LastUpdatedAt = load.State!.LastUpdatedAt
                    };
                }

                return true;
            case RuntimeStateLoadStatus.Corrupt:
                SetFaulted("The runtime state file is corrupt: " + JoinErrors(load.Errors));
                return false;
            case RuntimeStateLoadStatus.IoFailure:
                SetFaulted("Failed to read the runtime state file: " + JoinErrors(load.Errors));
                return false;
            case RuntimeStateLoadStatus.Invalid:
            case RuntimeStateLoadStatus.UnsupportedVersion:
            default:
                SetFaulted("The runtime state is invalid: " + JoinErrors(load.Errors));
                return false;
        }
    }

    private async Task<bool> TryProcessDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow.ToUniversalTime();
        var due = GetActiveInstances()
            .Where(instance => IsDueNow(instance, now))
            .ToList();

        if (due.Count == 0)
        {
            return false;
        }

        if (due.Count == 1)
        {
            await AdvanceInstanceAsync(due[0], now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // 仲裁调用点：多个实例同时到期。
        var arbitration = _arbitrator.Arbitrate(due, now);
        if (arbitration.WinnerTaskId is { } winnerId && !arbitration.RequiresUserDecision)
        {
            // 正式赢家 + 重排逻辑归 T06；T04 占位仲裁（NoOp）对多到期恒返回 RequiresUserDecision。
            SetFaulted(
                $"Arbitration selected winner {winnerId}, but automatic winner/loser scheduling is not implemented (T06): "
                + arbitration.DecisionReason);
        }
        else
        {
            SetFaulted(
                "Multiple tasks are due simultaneously and require user arbitration: "
                + arbitration.DecisionReason);
        }

        return true;
    }

    private async Task AdvanceInstanceAsync(
        TaskInstance instance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (instance.State)
        {
            case TaskInstanceState.Waiting:
                if (instance.WarningStartTime is { } warningStart
                    && warningStart <= now
                    && instance.ScheduledFireTime > now)
                {
                    await EnterConfirmingAsync(instance, now, cancellationToken).ConfigureAwait(false);
                }
                else if (instance.ScheduledFireTime <= now)
                {
                    await EnterExecutingAsync(instance, now, cancellationToken).ConfigureAwait(false);
                }

                break;
            case TaskInstanceState.Confirming:
                if (instance.ScheduledFireTime <= now)
                {
                    await EnterExecutingAsync(instance, now, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    private async Task EnterConfirmingAsync(
        TaskInstance instance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // waiting → running（瞬时，不落盘）→ confirming。
        var running = _stateMachine.TryTransition(
            instance.State,
            TaskInstanceState.Running,
            TaskInstanceStateTransitionCause.ScheduleTriggered,
            SourceName);
        if (!running.Allowed)
        {
            return;
        }

        var confirming = _stateMachine.TryTransition(
            TaskInstanceState.Running,
            TaskInstanceState.Confirming,
            TaskInstanceStateTransitionCause.PipelineCompleted,
            SourceName);
        if (!confirming.Allowed)
        {
            return;
        }

        var updated = instance with { State = TaskInstanceState.Confirming };
        await PersistAndCommitInstanceAsync(updated, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnterExecutingAsync(
        TaskInstance instance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (instance.HasExecuted)
        {
            return;
        }

        if (instance.State == TaskInstanceState.Waiting)
        {
            // 无告警窗口：waiting → running（瞬时）→ confirming（瞬时）→ executing。
            var running = _stateMachine.TryTransition(
                instance.State,
                TaskInstanceState.Running,
                TaskInstanceStateTransitionCause.ScheduleTriggered,
                SourceName);
            if (!running.Allowed)
            {
                return;
            }

            var confirming = _stateMachine.TryTransition(
                TaskInstanceState.Running,
                TaskInstanceState.Confirming,
                TaskInstanceStateTransitionCause.PipelineCompleted,
                SourceName);
            if (!confirming.Allowed)
            {
                return;
            }
        }

        var executingTransition = _stateMachine.TryTransition(
            TaskInstanceState.Confirming,
            TaskInstanceState.Executing,
            TaskInstanceStateTransitionCause.PowerConfirmed,
            SourceName);
        if (!executingTransition.Allowed)
        {
            return;
        }

        var executing = instance with
        {
            State = TaskInstanceState.Executing,
            HasExecuted = true
        };

        if (!await PersistAndCommitInstanceAsync(executing, now, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _handler.HandleDueAsync(executing, cancellationToken).ConfigureAwait(false);
        }
        catch (ScheduledTaskHandlingException)
        {
            await EnterTerminalAsync(
                executing,
                TaskInstanceState.Faulted,
                TaskInstanceStateTransitionCause.PowerFailed,
                _clock.UtcNow.ToUniversalTime(),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            SetFaulted("The due task handler failed: " + exception.Message);
            return;
        }

        await EnterTerminalAsync(
            executing,
            TaskInstanceState.Executed,
            TaskInstanceStateTransitionCause.PowerCompleted,
            _clock.UtcNow.ToUniversalTime(),
            cancellationToken).ConfigureAwait(false);

        await TryRescheduleRecurringAsync(executing, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnterTerminalAsync(
        TaskInstance instance,
        TaskInstanceState target,
        TaskInstanceStateTransitionCause cause,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transition = _stateMachine.TryTransition(instance.State, target, cause, SourceName);
        if (!transition.Allowed)
        {
            SetFaulted("The state machine rejected the terminal transition: " + transition.Reason);
            return;
        }

        var terminal = instance with { State = target };
        await PersistAndCommitInstanceAsync(terminal, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRescheduleRecurringAsync(
        TaskInstance executed,
        CancellationToken cancellationToken)
    {
        var definition = _taskService.Get(executed.SourceTaskId);
        if (definition is null || !definition.IsEnabled || definition.Kind != TaskKind.DailyAt)
        {
            return;
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var result = _taskService.RescheduleDaily(
            definition,
            executed,
            now,
            _clock.LocalTimeZone);

        if (result.Succeeded && result.Instance is not null)
        {
            await PersistAndCommitInstanceAsync(result.Instance, now, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<SchedulerCommandResult> DispatchCommandAsync(
        SchedulerCommand command,
        CancellationToken cancellationToken)
    {
        return command switch
        {
            CreateTaskCommand create => await HandleCreateAsync(create, cancellationToken).ConfigureAwait(false),
            SnoozeTaskCommand snooze => await HandleSnoozeAsync(snooze, cancellationToken).ConfigureAwait(false),
            CancelTaskCommand cancel => await HandleCancelAsync(cancel, cancellationToken).ConfigureAwait(false),
            ClearTerminalTaskCommand clear => await HandleClearTerminalAsync(clear, cancellationToken).ConfigureAwait(false),
            SetTaskEnabledCommand setEnabled => HandleSetEnabledAsync(setEnabled),
            _ => Rejected(GetSnapshot(), SchedulerCommandStatus.InvalidCommand, "Unknown command type.")
        };
    }

    private async Task<SchedulerCommandResult> HandleCreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instances = GetSnapshot().Instances;
        if (instances.TryGetValue(command.Definition.Id, out var existing)
            && !IsTerminal(existing.State))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.ActiveTaskExists,
                "A live instance for this task already exists.");
        }

        var taskResult = _taskService.Create(
            command.Definition,
            _clock.UtcNow,
            _clock.LocalTimeZone);
        if (!taskResult.Succeeded)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TaskServiceRejected,
                taskResult.Message,
                taskResult.Status,
                taskResult.TransitionDecisionCode);
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var next = WithInstance(instances, taskResult.Instance!);
        if (!await PersistAndCommitAsync(next, now, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.PersistenceFailed,
                "Failed to persist the created task.");
        }

        return Success("The task was created.");
    }

    private async Task<SchedulerCommandResult> HandleSnoozeAsync(
        SnoozeTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instance = FindInstance(command.ExpectedInstanceId);
        if (instance is null)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.NoCurrentTask,
                "No instance matches the expected instance id.");
        }

        if (command.ExpectedStageToken != instance.StageToken)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.StaleCommand,
                "The stage token does not match.");
        }

        var taskResult = _taskService.Snooze(instance, command.Duration, _clock.UtcNow);
        if (!taskResult.Succeeded)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TaskServiceRejected,
                taskResult.Message,
                taskResult.Status,
                taskResult.TransitionDecisionCode);
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var next = WithInstance(GetSnapshot().Instances, taskResult.Instance!);
        if (!await PersistAndCommitAsync(next, now, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.PersistenceFailed,
                "Failed to persist the snoozed task.");
        }

        return Success("The task was snoozed.");
    }

    private async Task<SchedulerCommandResult> HandleCancelAsync(
        CancelTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instance = FindInstance(command.ExpectedInstanceId);
        if (instance is null)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.NoCurrentTask,
                "No instance matches the expected instance id.");
        }

        if (command.ExpectedStageToken != instance.StageToken)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.StaleCommand,
                "The stage token does not match.");
        }

        var taskResult = _taskService.Cancel(instance);
        if (!taskResult.Succeeded)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TaskServiceRejected,
                taskResult.Message,
                taskResult.Status,
                taskResult.TransitionDecisionCode);
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var next = WithInstance(GetSnapshot().Instances, taskResult.Instance!);
        if (!await PersistAndCommitAsync(next, now, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.PersistenceFailed,
                "Failed to persist the cancelled task.");
        }

        return Success("The task was cancelled.");
    }

    private async Task<SchedulerCommandResult> HandleClearTerminalAsync(
        ClearTerminalTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instance = FindInstance(command.ExpectedInstanceId);
        if (instance is null)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.NoCurrentTask,
                "No instance matches the expected instance id.");
        }

        if (!IsTerminal(instance.State))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TransitionRejected,
                "ClearTerminal only applies to terminal states.");
        }

        // V2 无 Idle 态：直接移除终态实例。
        var now = _clock.UtcNow.ToUniversalTime();
        var next = WithoutInstance(GetSnapshot().Instances, instance.SourceTaskId);
        if (!await PersistAndCommitAsync(next, now, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.PersistenceFailed,
                "Failed to persist the cleared task.");
        }

        return Success("The terminal instance was cleared.");
    }

    private SchedulerCommandResult HandleSetEnabledAsync(SetTaskEnabledCommand command)
    {
        var result = _taskService.SetEnabled(command.TaskId, command.IsEnabled);
        if (!result.Succeeded)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TaskServiceRejected,
                result.Message);
        }

        return Success("The task enable state was updated.");
    }

    private async Task ProcessCommandAsync(
        PendingCommand pending,
        CancellationToken cancellationToken)
    {
        SchedulerCommandResult result;
        try
        {
            result = await DispatchCommandAsync(pending.Command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Rejected(GetSnapshot(), SchedulerCommandStatus.NotRunning, "The engine is stopping.");
        }
        catch (Exception exception)
        {
            result = Rejected(GetSnapshot(), SchedulerCommandStatus.Faulted, "The command failed: " + exception.Message);
        }

        pending.Tcs.TrySetResult(result);
    }

    private async Task<bool> PersistAndCommitInstanceAsync(
        TaskInstance updated,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var next = WithInstance(GetSnapshot().Instances, updated);
        return await PersistAndCommitAsync(next, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PersistAndCommitAsync(
        IReadOnlyDictionary<Guid, TaskInstance> instances,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeState.CurrentSchemaVersion,
            Instances = instances,
            LastUpdatedAt = now
        };

        var write = await _runtimeStateStore.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!write.Succeeded)
        {
            SetFaulted("Failed to persist the runtime state: " + JoinErrors(write.Errors));
            return false;
        }

        lock (_sync)
        {
            _snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                Instances = instances,
                LastUpdatedAt = now
            };
        }

        return true;
    }

    private Task ShutdownPendingCommandsAsync()
    {
        List<PendingCommand> pendingCommands;
        lock (_sync)
        {
            pendingCommands = new List<PendingCommand>();
            while (_channel.Reader.TryRead(out var pending))
            {
                pendingCommands.Add(pending);
            }

            if (_snapshot.EngineStatus != SchedulerEngineStatus.Faulted)
            {
                _snapshot = _snapshot with { EngineStatus = SchedulerEngineStatus.Stopped };
            }
        }

        foreach (var pending in pendingCommands)
        {
            pending.Tcs.TrySetResult(Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.NotRunning,
                "The engine is stopping."));
        }

        return Task.CompletedTask;
    }

    private void SetFaulted(string message)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                EngineStatus = SchedulerEngineStatus.Faulted,
                FaultMessage = message
            };
        }
    }

    private TaskInstance? FindInstance(Guid instanceId)
        => GetSnapshot().Instances.Values.FirstOrDefault(instance => instance.InstanceId == instanceId);

    private IReadOnlyList<TaskInstance> GetActiveInstances()
        => GetSnapshot().Instances.Values
            .Where(instance => instance.State is TaskInstanceState.Waiting or TaskInstanceState.Confirming)
            .ToList();

    private DateTimeOffset? ComputeNextDeadline(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var instance in GetActiveInstances())
        {
            var deadline = ComputeFutureDeadline(instance, now);
            if (deadline is null)
            {
                continue;
            }

            if (next is null || deadline.Value < next.Value)
            {
                next = deadline.Value;
            }
        }

        return next;
    }

    private static bool IsDueNow(TaskInstance instance, DateTimeOffset now)
    {
        return instance.State switch
        {
            TaskInstanceState.Waiting => instance.ScheduledFireTime <= now
                || (instance.WarningStartTime is { } warningStart && warningStart <= now),
            TaskInstanceState.Confirming => instance.ScheduledFireTime <= now,
            _ => false
        };
    }

    private static DateTimeOffset? ComputeFutureDeadline(TaskInstance instance, DateTimeOffset now)
    {
        return instance.State switch
        {
            TaskInstanceState.Waiting when instance.WarningStartTime is { } warningStart && warningStart > now =>
                warningStart,
            TaskInstanceState.Waiting when instance.ScheduledFireTime > now => instance.ScheduledFireTime,
            TaskInstanceState.Confirming when instance.ScheduledFireTime > now => instance.ScheduledFireTime,
            _ => null
        };
    }

    private static bool IsTerminal(TaskInstanceState state)
        => state is TaskInstanceState.Cancelled
            or TaskInstanceState.Executed
            or TaskInstanceState.Faulted
            or TaskInstanceState.Interrupted;

    private static IReadOnlyDictionary<Guid, TaskInstance> WithInstance(
        IReadOnlyDictionary<Guid, TaskInstance> current,
        TaskInstance updated)
    {
        var copy = new Dictionary<Guid, TaskInstance>(current)
        {
            [updated.SourceTaskId] = updated
        };
        return copy;
    }

    private static IReadOnlyDictionary<Guid, TaskInstance> WithoutInstance(
        IReadOnlyDictionary<Guid, TaskInstance> current,
        Guid taskId)
    {
        var copy = new Dictionary<Guid, TaskInstance>(current);
        copy.Remove(taskId);
        return copy;
    }

    private static string JoinErrors(IReadOnlyList<string> errors)
        => errors.Count == 0 ? "unknown error." : string.Join(" ", errors);

    private SchedulerCommandResult Success(string message) => new()
    {
        Status = SchedulerCommandStatus.Success,
        Snapshot = GetSnapshot(),
        Message = message
    };

    private SchedulerCommandResult Rejected(
        SchedulerSnapshot snapshot,
        SchedulerCommandStatus status,
        string message,
        TaskCommandStatus? taskCommandStatus = null,
        TaskInstanceStateTransitionDecisionCode? transitionDecisionCode = null) => new()
        {
            Status = status,
            TaskCommandStatus = taskCommandStatus,
            TransitionDecisionCode = transitionDecisionCode,
            Snapshot = snapshot,
            Message = message
        };

    private static async Task SuppressAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct PendingCommand(
        SchedulerCommand Command,
        TaskCompletionSource<SchedulerCommandResult> Tcs);
}
