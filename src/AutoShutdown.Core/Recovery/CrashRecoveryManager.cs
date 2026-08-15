using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Recovery;

/// <summary>
/// 崩溃恢复（架构 §5 修正 2）：启动链在调度循环前调用，将未完成的瞬态实例
/// （running / confirming / executing）标记为 interrupted，且绝不补执行。
/// waiting 实例保持 waiting（由调度循环重算排程）；终态保持；恢复只在确有
/// 瞬态实例时写入一次。纯逻辑，不写日志、不触发通知、不接触电源。
/// </summary>
public sealed class CrashRecoveryManager
{
    private const string SourceName = "CrashRecoveryManager";

    private readonly RuntimeStateStore _store;
    private readonly ITaskInstanceStateMachine _stateMachine;
    private readonly IClock _clock;

    public CrashRecoveryManager(
        IStorage storage,
        ITaskInstanceStateMachine stateMachine,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(clock);

        _store = new RuntimeStateStore(storage);
        _stateMachine = stateMachine;
        _clock = clock;
    }

    public async Task<CrashRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        switch (load.Status)
        {
            case RuntimeStateLoadStatus.NotFound:
                return new CrashRecoveryResult { Status = CrashRecoveryStatus.NotFound };
            case RuntimeStateLoadStatus.Corrupt:
                return new CrashRecoveryResult
                {
                    Status = CrashRecoveryStatus.Corrupt,
                    Errors = load.Errors
                };
            case RuntimeStateLoadStatus.IoFailure:
                return new CrashRecoveryResult
                {
                    Status = CrashRecoveryStatus.IoFailure,
                    Errors = load.Errors
                };
            case RuntimeStateLoadStatus.Invalid:
                return new CrashRecoveryResult
                {
                    Status = CrashRecoveryStatus.Invalid,
                    Errors = load.Errors
                };
            case RuntimeStateLoadStatus.UnsupportedVersion:
                return new CrashRecoveryResult
                {
                    Status = CrashRecoveryStatus.UnsupportedVersion,
                    Errors = load.Errors
                };
            case RuntimeStateLoadStatus.Success:
            case RuntimeStateLoadStatus.Migrated:
                break;
            default:
                return new CrashRecoveryResult
                {
                    Status = CrashRecoveryStatus.IoFailure,
                    Errors = load.Errors
                };
        }

        var state = load.State!;

        if (!TryInterruptTransientInstances(
                state.Instances,
                out var instances,
                out var interruptedTaskIds))
        {
            return new CrashRecoveryResult
            {
                Status = CrashRecoveryStatus.TransitionRejected,
                Errors = ["A transient instance could not be transitioned to Interrupted."]
            };
        }

        if (interruptedTaskIds.Count == 0)
        {
            // 无瞬态实例：不写文件，直接返回原始状态。
            return new CrashRecoveryResult
            {
                Status = CrashRecoveryStatus.NoRecoveryNeeded,
                State = state
            };
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var candidate = new RuntimeState
        {
            SchemaVersion = RuntimeState.CurrentSchemaVersion,
            Instances = instances,
            LastUpdatedAt = now
        };

        var write = await _store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!write.Succeeded)
        {
            return new CrashRecoveryResult
            {
                Status = CrashRecoveryStatus.IoFailure,
                Errors = write.Errors
            };
        }

        return new CrashRecoveryResult
        {
            Status = CrashRecoveryStatus.Recovered,
            State = candidate,
            InterruptedTaskIds = interruptedTaskIds
        };
    }

    private bool TryInterruptTransientInstances(
        IReadOnlyDictionary<Guid, TaskInstance> source,
        out IReadOnlyDictionary<Guid, TaskInstance> result,
        out IReadOnlyList<Guid> interruptedTaskIds)
    {
        var copy = new Dictionary<Guid, TaskInstance>(source);
        var interrupted = new List<Guid>();
        result = copy;
        interruptedTaskIds = interrupted;

        foreach (var (taskId, instance) in source)
        {
            if (instance.State is not (
                TaskInstanceState.Running
                or TaskInstanceState.Confirming
                or TaskInstanceState.Executing))
            {
                continue;
            }

            var transition = _stateMachine.TryTransition(
                instance.State,
                TaskInstanceState.Interrupted,
                TaskInstanceStateTransitionCause.CrashRecovered,
                SourceName);
            if (!transition.Allowed)
            {
                return false;
            }

            copy[taskId] = instance with { State = TaskInstanceState.Interrupted };
            interrupted.Add(taskId);
        }

        return true;
    }
}
