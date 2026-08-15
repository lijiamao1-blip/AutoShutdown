using System.Threading.Channels;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Workflow;

namespace AutoShutdown.Core.Scheduling;

public sealed class SchedulerEngine : ISchedulerEngine
{
    private const string RuntimeFileName = "runtime.json";
    private const int RuntimeSchemaVersion = 1;

    private readonly Channel<PendingCommand> _channel;
    private readonly IStorage _storage;
    private readonly IClock _clock;
    private readonly IAsyncDeadline _deadline;
    private readonly ITaskService _taskService;
    private readonly ITaskStateMachine _stateMachine;
    private readonly IIdentifierGenerator _identifierGenerator;
    private readonly IScheduledTaskHandler _handler;
    private readonly object _sync = new();

    private SchedulerSnapshot _snapshot = SchedulerSnapshot.Empty;
    private Task? _runTask;

    public SchedulerEngine(
        IStorage storage,
        IClock clock,
        IAsyncDeadline deadline,
        ITaskService taskService,
        ITaskStateMachine stateMachine,
        IIdentifierGenerator identifierGenerator,
        IScheduledTaskHandler handler)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(deadline);
        ArgumentNullException.ThrowIfNull(taskService);
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(identifierGenerator);
        ArgumentNullException.ThrowIfNull(handler);

        _storage = storage;
        _clock = clock;
        _deadline = deadline;
        _taskService = taskService;
        _stateMachine = stateMachine;
        _identifierGenerator = identifierGenerator;
        _handler = handler;

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

            if (await TryHandleImmediateAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            snapshot = GetSnapshot();
            var now = _clock.UtcNow;
            var instance = snapshot.CurrentInstance;
            var deadline = instance is not null ? ComputeDeadline(instance, now) : null;
            var context = deadline is not null && instance is not null
                ? new DeadlineContext(instance.InstanceId, instance.StageToken, instance.State, deadline.Value)
                : (DeadlineContext?)null;

            using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readTask = _channel.Reader.ReadAsync(iterationCts.Token).AsTask();
            Task? deadlineTask = context is not null
                ? _deadline.WaitUntilAsync(context.Value.UtcDeadline, iterationCts.Token)
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
                await OnDeadlineExpiredAsync(
                    context!.Value,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryRestoreAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<RuntimeState>(RuntimeFileName, cancellationToken)
            .ConfigureAwait(false);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                lock (_sync)
                {
                    _snapshot = new SchedulerSnapshot
                    {
                        EngineStatus = SchedulerEngineStatus.Running,
                        CurrentInstance = null,
                        LastUpdatedAt = _clock.UtcNow.ToUniversalTime()
                    };
                }

                return true;
            case StorageReadStatus.Corrupt:
                SetFaulted("The runtime state file is corrupt: " + (read.Error ?? "unknown error."));
                return false;
            case StorageReadStatus.IoFailure:
                SetFaulted("Failed to read the runtime state file: " + (read.Error ?? "unknown error."));
                return false;
            case StorageReadStatus.Success:
                break;
            default:
                SetFaulted("The storage layer returned an unknown status while reading the runtime state.");
                return false;
        }

        var state = read.Value;
        if (state is null || state.SchemaVersion != RuntimeSchemaVersion)
        {
            SetFaulted("The runtime state is missing or has an unsupported schema version.");
            return false;
        }

        if (state.LastUpdatedAt == default)
        {
            SetFaulted("The runtime state has an invalid LastUpdatedAt.");
            return false;
        }

        var instance = state.CurrentInstance;
        if (instance is not null
            && (instance.InstanceId == Guid.Empty
                || instance.SourceTaskId == Guid.Empty
                || instance.StageToken == Guid.Empty))
        {
            SetFaulted("The runtime instance has empty identity fields.");
            return false;
        }

        var now = _clock.UtcNow.ToUniversalTime();

        if (instance is not null && instance.State is TaskState.Warning or TaskState.Executing)
        {
            var transition = _stateMachine.TryTransition(
                instance.State,
                TaskState.Interrupted,
                TaskTransitionCause.RecoveryInterrupted);
            if (!transition.Allowed)
            {
                SetFaulted("The recovered instance cannot be interrupted: " + transition.Message);
                return false;
            }

            var interrupted = instance with { State = TaskState.Interrupted };
            var candidate = new RuntimeState
            {
                SchemaVersion = RuntimeSchemaVersion,
                CurrentInstance = interrupted,
                LastUpdatedAt = now
            };

            var write = await _storage.WriteAsync(RuntimeFileName, candidate, cancellationToken)
                .ConfigureAwait(false);
            if (!write.Succeeded)
            {
                SetFaulted("Failed to persist the recovered state: " + (write.Error ?? "unknown error."));
                return false;
            }

            lock (_sync)
            {
                _snapshot = new SchedulerSnapshot
                {
                    EngineStatus = SchedulerEngineStatus.Running,
                    CurrentInstance = interrupted,
                    LastUpdatedAt = now
                };
            }

            return true;
        }

        if (instance is not null && instance.State == TaskState.Scheduled && instance.ScheduledFireTime <= now)
        {
            lock (_sync)
            {
                _snapshot = new SchedulerSnapshot
                {
                    EngineStatus = SchedulerEngineStatus.Running,
                    CurrentInstance = instance,
                    LastUpdatedAt = state.LastUpdatedAt
                };
            }

            SetFaulted("Recovery found a missed task; the instance is preserved for manual resolution.");
            return false;
        }

        lock (_sync)
        {
            _snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                CurrentInstance = instance,
                LastUpdatedAt = state.LastUpdatedAt
            };
        }

        return true;
    }

    private async Task<bool> TryHandleImmediateAsync(CancellationToken cancellationToken)
    {
        var instance = GetSnapshot().CurrentInstance;
        if (instance is null)
        {
            return false;
        }

        var now = _clock.UtcNow.ToUniversalTime();

        if (instance.State == TaskState.Scheduled)
        {
            if (instance.WarningStartTime is { } warningStart
                && warningStart <= now
                && instance.ScheduledFireTime > now)
            {
                await EnterWarningAsync(instance, now, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (instance.ScheduledFireTime <= now)
            {
                await EnterExecutingAsync(instance, now, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        else if (instance.State == TaskState.Warning && instance.ScheduledFireTime <= now)
        {
            await EnterExecutingAsync(instance, now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task OnDeadlineExpiredAsync(
        DeadlineContext context,
        CancellationToken cancellationToken)
    {
        var instance = GetSnapshot().CurrentInstance;
        if (instance is null)
        {
            return;
        }

        if (instance.InstanceId != context.InstanceId
            || instance.StageToken != context.StageToken
            || instance.State != context.ExpectedState)
        {
            return;
        }

        var now = _clock.UtcNow.ToUniversalTime();

        if (instance.State == TaskState.Scheduled)
        {
            if (instance.WarningStartTime is { } warningStart
                && warningStart <= now
                && instance.ScheduledFireTime > now)
            {
                await EnterWarningAsync(instance, now, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (instance.ScheduledFireTime <= now)
            {
                await EnterExecutingAsync(instance, now, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (instance.State == TaskState.Warning && instance.ScheduledFireTime <= now)
        {
            await EnterExecutingAsync(instance, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnterWarningAsync(
        TaskInstance instance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transition = _stateMachine.TryTransition(
            instance.State,
            TaskState.Warning,
            TaskTransitionCause.WarningDue);
        if (!transition.Allowed)
        {
            return;
        }

        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeSchemaVersion,
            CurrentInstance = instance with { State = TaskState.Warning },
            LastUpdatedAt = now
        };

        await PersistAndCommitAsync(candidate, cancellationToken).ConfigureAwait(false);
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

        var transition = _stateMachine.TryTransition(
            instance.State,
            TaskState.Executing,
            TaskTransitionCause.ExecuteDue);
        if (!transition.Allowed)
        {
            return;
        }

        var executed = instance with
        {
            State = TaskState.Executing,
            HasExecuted = true
        };
        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeSchemaVersion,
            CurrentInstance = executed,
            LastUpdatedAt = now
        };

        if (!await PersistAndCommitAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _handler.HandleDueAsync(executed, cancellationToken).ConfigureAwait(false);
        }
        catch (ScheduledTaskHandlingException)
        {
            await EnterTerminalStateAsync(
                executed,
                TaskState.Failed,
                TaskTransitionCause.PowerFailed,
                _clock.UtcNow.ToUniversalTime(),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            SetFaulted("The due task handler failed: " + exception.Message);
            return;
        }

        await EnterTerminalStateAsync(
            executed,
            TaskState.Completed,
            TaskTransitionCause.PowerAccepted,
            _clock.UtcNow.ToUniversalTime(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnterTerminalStateAsync(
        TaskInstance instance,
        TaskState target,
        TaskTransitionCause cause,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transition = _stateMachine.TryTransition(instance.State, target, cause);
        if (!transition.Allowed)
        {
            SetFaulted("The state machine rejected the terminal transition: " + transition.Message);
            return;
        }

        var terminal = instance with { State = target };
        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeSchemaVersion,
            CurrentInstance = terminal,
            LastUpdatedAt = now
        };

        if (!await PersistAndCommitAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return;
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
            _ => Rejected(GetSnapshot(), SchedulerCommandStatus.InvalidCommand, "Unknown command type.")
        };
    }

    private async Task<SchedulerCommandResult> HandleCreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instance = GetSnapshot().CurrentInstance;
        if (instance is not null && instance.State != TaskState.Idle)
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.ActiveTaskExists, "An active task instance already exists.");
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

        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeSchemaVersion,
            CurrentInstance = taskResult.Instance,
            LastUpdatedAt = _clock.UtcNow.ToUniversalTime()
        };

        if (!await PersistAndCommitAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.PersistenceFailed, "Failed to persist the created task.");
        }

        return Success("The task was created.");
    }

    private async Task<SchedulerCommandResult> HandleSnoozeAsync(
        SnoozeTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instance = GetSnapshot().CurrentInstance;
        if (instance is null)
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.NoCurrentTask, "There is no current task.");
        }

        if (command.ExpectedInstanceId != instance.InstanceId || command.ExpectedStageToken != instance.StageToken)
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.StaleCommand, "The instance identity or stage token does not match.");
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

        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeSchemaVersion,
            CurrentInstance = taskResult.Instance,
            LastUpdatedAt = _clock.UtcNow.ToUniversalTime()
        };

        if (!await PersistAndCommitAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.PersistenceFailed, "Failed to persist the snoozed task.");
        }

        return Success("The task was snoozed.");
    }

    private async Task<SchedulerCommandResult> HandleCancelAsync(
        CancelTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instance = GetSnapshot().CurrentInstance;
        if (instance is null)
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.NoCurrentTask, "There is no current task.");
        }

        if (command.ExpectedInstanceId != instance.InstanceId || command.ExpectedStageToken != instance.StageToken)
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.StaleCommand, "The instance identity or stage token does not match.");
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

        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeSchemaVersion,
            CurrentInstance = taskResult.Instance,
            LastUpdatedAt = _clock.UtcNow.ToUniversalTime()
        };

        if (!await PersistAndCommitAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.PersistenceFailed, "Failed to persist the cancelled task.");
        }

        return Success("The task was cancelled.");
    }

    private async Task<SchedulerCommandResult> HandleClearTerminalAsync(
        ClearTerminalTaskCommand command,
        CancellationToken cancellationToken)
    {
        var instance = GetSnapshot().CurrentInstance;
        if (instance is null)
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.NoCurrentTask, "There is no current task.");
        }

        if (command.ExpectedInstanceId != instance.InstanceId)
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.StaleCommand, "The instance identity does not match.");
        }

        if (instance.State is not (TaskState.Cancelled or TaskState.Completed or TaskState.Failed or TaskState.Interrupted))
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.TransitionRejected, "ClearTerminal only applies to terminal states.");
        }

        var transition = _stateMachine.TryTransition(
            instance.State,
            TaskState.Idle,
            TaskTransitionCause.ClearTerminalState);
        if (!transition.Allowed)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TransitionRejected,
                transition.Message,
                null,
                transition.DecisionCode);
        }

        var stageToken = _identifierGenerator.NewId();
        if (stageToken == Guid.Empty || stageToken == instance.StageToken || stageToken == instance.InstanceId)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TaskServiceRejected,
                "The generated stage token is not valid.");
        }

        var cleared = instance with
        {
            State = TaskState.Idle,
            WarningStartTime = null,
            StageToken = stageToken
        };
        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeSchemaVersion,
            CurrentInstance = cleared,
            LastUpdatedAt = _clock.UtcNow.ToUniversalTime()
        };

        if (!await PersistAndCommitAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(GetSnapshot(), SchedulerCommandStatus.PersistenceFailed, "Failed to persist the cleared task.");
        }

        return Success("The terminal state was cleared.");
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

    private async Task<bool> PersistAndCommitAsync(
        RuntimeState candidate,
        CancellationToken cancellationToken)
    {
        var write = await _storage.WriteAsync(RuntimeFileName, candidate, cancellationToken)
            .ConfigureAwait(false);

        if (!write.Succeeded)
        {
            SetFaulted("Failed to persist the runtime state: " + (write.Error ?? "unknown error."));
            return false;
        }

        lock (_sync)
        {
            _snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                CurrentInstance = candidate.CurrentInstance,
                LastUpdatedAt = candidate.LastUpdatedAt
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
        TaskTransitionDecisionCode? transitionDecisionCode = null) => new()
        {
            Status = status,
            TaskCommandStatus = taskCommandStatus,
            TransitionDecisionCode = transitionDecisionCode,
            Snapshot = snapshot,
            Message = message
        };

    private static DateTimeOffset? ComputeDeadline(TaskInstance instance, DateTimeOffset now)
    {
        if (instance.State == TaskState.Scheduled)
        {
            if (instance.WarningStartTime is { } warningStart && warningStart > now)
            {
                return warningStart;
            }

            return instance.ScheduledFireTime;
        }

        if (instance.State == TaskState.Warning)
        {
            return instance.ScheduledFireTime;
        }

        return null;
    }

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

    private readonly record struct DeadlineContext(
        Guid InstanceId,
        Guid StageToken,
        TaskState ExpectedState,
        DateTimeOffset UtcDeadline);

    private readonly record struct PendingCommand(
        SchedulerCommand Command,
        TaskCompletionSource<SchedulerCommandResult> Tcs);
}
