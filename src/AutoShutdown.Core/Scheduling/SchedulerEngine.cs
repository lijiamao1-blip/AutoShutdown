using System.Threading.Channels;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Unattended;
using AutoShutdown.Core.Workflow;

namespace AutoShutdown.Core.Scheduling;

public sealed class SchedulerEngine : ISchedulerEngine
{
    private const string SourceName = "SchedulerEngine";

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<PendingCommand> _channel;
    private readonly IClock _clock;
    private readonly IAsyncDeadline _deadline;
    private readonly ITaskService _taskService;
    private readonly ITaskInstanceStateMachine _stateMachine;
    private readonly IIdentifierGenerator _identifierGenerator;
    private readonly IScheduledTaskHandler _handler;
    private readonly ITaskArbitrator _arbitrator;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly TasksDocumentStore _tasksDocumentStore;
    private readonly IIdleMonitor? _idleMonitor;
    private readonly TimeSpan _globalDefaultIdleThreshold;
    private readonly IUnattendedPolicyService? _unattendedPolicyService;
    private readonly UnattendedConfirmationEvaluator? _unattendedEvaluator;
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
        ITaskArbitrator arbitrator,
        IIdleMonitor? idleMonitor = null,
        TimeSpan? globalDefaultIdleThreshold = null,
        IUnattendedPolicyService? unattendedPolicyService = null,
        UnattendedConfirmationEvaluator? unattendedEvaluator = null)
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
        _idleMonitor = idleMonitor;
        _globalDefaultIdleThreshold = globalDefaultIdleThreshold ?? IdleShutdownRule.GlobalDefaultThreshold;
        _unattendedPolicyService = unattendedPolicyService;
        _unattendedEvaluator = unattendedEvaluator;
        _runtimeStateStore = new RuntimeStateStore(storage);
        _tasksDocumentStore = new TasksDocumentStore(storage);

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

        _idleMonitor?.Start();
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
            _idleMonitor?.Stop();
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

            if (await TryEvaluateIdleAsync(cancellationToken).ConfigureAwait(false))
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

                break;
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

                break;
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

        // S14：载入任务定义（tasks.json）并登记到领域集合，供周期改期与重启恢复使用。
        return await TryLoadDefinitionsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 载入任务定义（tasks.json）到 <see cref="ITaskService"/> 领域集合。NotFound 视为空清单；
    /// Corrupt/Invalid/UnsupportedVersion/IoFailure 一律 SetFaulted（损坏数据绝不静默回退）。
    /// 单个规则登记失败不 SetFaulted 整机——损坏规则只使对应任务无法改期，不影响其他任务。
    /// </summary>
    private async Task<bool> TryLoadDefinitionsAsync(CancellationToken cancellationToken)
    {
        var load = await _tasksDocumentStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        switch (load.Status)
        {
            case TasksLoadStatus.NotFound:
                return true;
            case TasksLoadStatus.Success:
            case TasksLoadStatus.Migrated:
                break;
            case TasksLoadStatus.Corrupt:
                SetFaulted("The task definitions file is corrupt: " + JoinErrors(load.Errors));
                return false;
            case TasksLoadStatus.IoFailure:
                SetFaulted("Failed to read the task definitions file: " + JoinErrors(load.Errors));
                return false;
            case TasksLoadStatus.Invalid:
            case TasksLoadStatus.UnsupportedVersion:
            default:
                SetFaulted("The task definitions are invalid: " + JoinErrors(load.Errors));
                return false;
        }

        foreach (var definition in load.Document!.Tasks)
        {
            // 结构校验已在 TasksDocumentStore.LoadAsync 完成；此处登记失败（重复 Id 等）
            // 属防御性兜底，跳过该规则而不影响其它规则。
            _ = RegisterDefinition(definition);
        }

        return true;
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

        // 待决仲裁（用户决策）期间：已纳入候选的实例不再重复仲裁，等待
        // ResolveArbitrationCommand；其它非候选到期实例按单实例正常推进。
        if (GetSnapshot().PendingArbitration is { } pending)
        {
            var unresolved = due
                .Where(instance => !pending.CandidateTaskIds.Contains(instance.SourceTaskId))
                .ToList();
            if (unresolved.Count == 0)
            {
                return false;
            }

            if (unresolved.Count == 1)
            {
                await AdvanceInstanceAsync(unresolved[0], now, cancellationToken).ConfigureAwait(false);
                return true;
            }

            due = unresolved;
        }

        if (due.Count == 1)
        {
            await AdvanceInstanceAsync(due[0], now, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // 仲裁调用点（S13-T06 决策器，T09 消费）：多个实例同时到期必须先仲裁，
        // 禁止并发绕过仲裁进入电源执行。
        var arbitration = _arbitrator.Arbitrate(due, now);

        if (arbitration.RequiresUserDecision)
        {
            // 强制冲突：不执行电源、不 SetFaulted；挂起并交由 UI 询问用户。
            SetPendingArbitration(arbitration, due);
            return false;
        }

        if (arbitration.WinnerTaskId is { } winnerId)
        {
            var applied = await ApplyArbitrationAsync(arbitration, due, now, cancellationToken)
                .ConfigureAwait(false);
            if (applied)
            {
                return true;
            }
        }

        SetFaulted(
            "Multiple tasks are due simultaneously but arbitration produced no actionable winner: "
            + arbitration.DecisionReason);
        return true;
    }

    /// <summary>
    /// 空闲评估（S15）：仅作用于 Idle 任务。Waiting 且空闲时长达到阈值 → 触发进入
    /// 倒计时/确认窗口（IsIdleTriggered）；由空闲触发的 Confirming 在输入恢复（空闲时长
    /// 回落到阈值以下）时取消。检测失败（idleDuration=null）默认不触发、不取消（fail-closed）。
    /// 非 Idle 任务完全不受影响。
    /// </summary>
    private async Task<bool> TryEvaluateIdleAsync(CancellationToken cancellationToken)
    {
        if (_idleMonitor is null)
        {
            return false;
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var idleCandidates = GetActiveInstances()
            .Select(instance => (Instance: instance, Definition: _taskService.Get(instance.SourceTaskId)))
            .Where(candidate => candidate.Definition?.Kind == TaskKind.Idle)
            .ToList();

        if (idleCandidates.Count == 0)
        {
            return false;
        }

        var idleDuration = _idleMonitor.GetIdleDuration();
        var changed = false;

        foreach (var (instance, definition) in idleCandidates)
        {
            if (definition is null)
            {
                continue;
            }

            switch (instance.State)
            {
                case TaskInstanceState.Waiting:
                    if (IdleShutdownRule.IsIdleDue(definition, _globalDefaultIdleThreshold, idleDuration))
                    {
                        changed |= await ArmIdleAsync(instance, definition, now, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case TaskInstanceState.Confirming when instance.IsIdleTriggered:
                    // 输入恢复取消仅在「检测成功且空闲时长明确低于有效阈值」时成立。
                    // 检测失败（idleDuration=null）必须 fail-closed：不取消、不标记恢复，
                    // 保持实例并留待下一轮轮询重新检测（独立验收缺陷修复）。
                    if (idleDuration is not null
                        && !IdleShutdownRule.IsIdleDue(definition, _globalDefaultIdleThreshold, idleDuration))
                    {
                        changed |= await CancelIdleTriggeredAsync(instance, now, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;
            }
        }

        return changed;
    }

    /// <summary>
    /// 触发空闲任务：Waiting → Confirming（倒计时/确认窗口），并设置真实触发时刻
    /// （now + 告警窗口；无告警窗口则立即到期）与 IsIdleTriggered 标记。字段级更新，
    /// 不改变冻结状态机白名单。
    /// </summary>
    private async Task<bool> ArmIdleAsync(
        TaskInstance instance,
        TaskDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var confirming = _stateMachine.TryTransition(
            instance.State,
            TaskInstanceState.Confirming,
            TaskInstanceStateTransitionCause.ScheduleTriggered,
            SourceName);
        if (!confirming.Allowed)
        {
            return false;
        }

        var warning = definition.WarningSeconds is > 0
            ? TimeSpan.FromSeconds(definition.WarningSeconds.Value)
            : TimeSpan.Zero;

        var armed = instance with
        {
            State = TaskInstanceState.Confirming,
            ScheduledFireTime = now.Add(warning).ToUniversalTime(),
            WarningStartTime = warning > TimeSpan.Zero ? now.ToUniversalTime() : null,
            IsIdleTriggered = true
        };

        return await PersistAndCommitInstanceAsync(armed, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>输入恢复取消：由空闲触发的 Confirming 通过冻结白名单 Confirming→Cancelled 终结。</summary>
    private async Task<bool> CancelIdleTriggeredAsync(
        TaskInstance instance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cancelled = _taskService.Cancel(instance);
        if (!cancelled.Succeeded || cancelled.Instance is null)
        {
            return false;
        }

        // 标记为「输入恢复取消」，与用户手动取消区分，供 UI 呈现恢复取消结果。
        var recovered = cancelled.Instance with { IsIdleRecovered = true };
        return await PersistAndCommitInstanceAsync(recovered, now, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsIdleInstance(TaskInstance instance)
        => _taskService.Get(instance.SourceTaskId)?.Kind == TaskKind.Idle;

    /// <summary>
    /// 消费仲裁结果（S13-T09）：赢家推进执行；合并（MergedTaskIds）任务在冻结状态机
    /// 内以取消承载「并入赢家执行」；改期（RescheduledTaskIds）的 Waiting 落选任务
    /// 字段级改期 ≥5 分钟并刷新 StageToken，Confirming 落选任务因白名单无回边而取消。
    /// </summary>
    private async Task<bool> ApplyArbitrationAsync(
        TaskArbitrationResult arbitration,
        IReadOnlyList<TaskInstance> due,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var winnerId = arbitration.WinnerTaskId!.Value;
        var winner = due.FirstOrDefault(instance => instance.SourceTaskId == winnerId);
        if (winner is null)
        {
            return false;
        }

        var next = new Dictionary<Guid, TaskInstance>(GetSnapshot().Instances);
        var errors = new List<string>();

        foreach (var taskId in arbitration.MergedTaskIds)
        {
            if (taskId == winnerId || !next.TryGetValue(taskId, out var loser))
            {
                continue;
            }

            // 同动作合并：落选任务并入赢家的一次电源操作，不再单独执行
            // （冻结白名单内唯一合法的「停止其独立执行」表达是 Cancelled）。
            var merged = _taskService.Cancel(loser);
            if (merged.Succeeded && merged.Instance is { } mergedInstance)
            {
                next[taskId] = mergedInstance;
            }
            else
            {
                errors.Add($"merged task {taskId} could not be cancelled");
            }
        }

        foreach (var taskId in arbitration.RescheduledTaskIds)
        {
            if (taskId == winnerId || !next.TryGetValue(taskId, out var loser))
            {
                continue;
            }

            if (loser.State == TaskInstanceState.Waiting)
            {
                var rescheduled = _taskService.RescheduleAfterArbitration(
                    loser,
                    TaskArbitrator.MinimumRescheduleDelay,
                    now);
                if (rescheduled.Succeeded && rescheduled.Instance is { } rescheduledInstance)
                {
                    next[taskId] = rescheduledInstance;
                }
                else
                {
                    errors.Add($"loser task {taskId} could not be rescheduled");
                }
            }
            else
            {
                // Confirming 落选任务已进入提醒窗口，无法在冻结白名单内改期回 Waiting；
                // 为防双重电源执行，按取消处理并如实记录。
                var cancelled = _taskService.Cancel(loser);
                if (cancelled.Succeeded && cancelled.Instance is { } cancelledInstance)
                {
                    next[taskId] = cancelledInstance;
                }
                else
                {
                    errors.Add($"confirming loser task {taskId} could not be cancelled");
                }
            }
        }

        if (errors.Count > 0)
        {
            SetFaulted("Arbitration loser handling failed: " + string.Join("; ", errors));
            return false;
        }

        if (!await PersistAndCommitAsync(next, now, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await AdvanceInstanceAsync(winner, now, cancellationToken).ConfigureAwait(false);
        SetLastArbitration(arbitration);
        return true;
    }

    /// <summary>
    /// 用户对强制冲突仲裁的决策：赢家立即执行，其余候选按 Waiting 改期 ≥5 分钟 /
    /// Confirming 取消；无待决仲裁或赢家不在候选中时拒绝。
    /// </summary>
    private async Task<SchedulerCommandResult> HandleResolveArbitrationAsync(
        ResolveArbitrationCommand command,
        CancellationToken cancellationToken)
    {
        var pending = GetSnapshot().PendingArbitration;
        if (pending is null)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.InvalidCommand,
                "No arbitration decision is pending.");
        }

        if (!pending.CandidateTaskIds.Contains(command.WinnerTaskId))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.InvalidCommand,
                "The chosen task is not part of the pending arbitration.");
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var next = new Dictionary<Guid, TaskInstance>(GetSnapshot().Instances);
        foreach (var taskId in pending.CandidateTaskIds)
        {
            if (taskId == command.WinnerTaskId || !next.TryGetValue(taskId, out var loser))
            {
                continue;
            }

            if (loser.State == TaskInstanceState.Waiting)
            {
                var rescheduled = _taskService.RescheduleAfterArbitration(
                    loser,
                    TaskArbitrator.MinimumRescheduleDelay,
                    now);
                if (rescheduled.Succeeded && rescheduled.Instance is { } rescheduledInstance)
                {
                    next[taskId] = rescheduledInstance;
                }
                else
                {
                    return Rejected(
                        GetSnapshot(),
                        SchedulerCommandStatus.TaskServiceRejected,
                        "Failed to reschedule the losing task: " + rescheduled.Message);
                }
            }
            else
            {
                var cancelled = _taskService.Cancel(loser);
                if (cancelled.Succeeded && cancelled.Instance is { } cancelledInstance)
                {
                    next[taskId] = cancelledInstance;
                }
                else
                {
                    return Rejected(
                        GetSnapshot(),
                        SchedulerCommandStatus.TaskServiceRejected,
                        "Failed to cancel the losing task: " + cancelled.Message);
                }
            }
        }

        if (!await PersistAndCommitAsync(next, now, cancellationToken).ConfigureAwait(false))
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.PersistenceFailed,
                "Failed to persist the arbitration resolution.");
        }

        var resolvedLosers = pending.CandidateTaskIds
            .Where(taskId => taskId != command.WinnerTaskId)
            .ToList();
        SetLastArbitration(new ArbitrationOutcome
        {
            WinnerTaskId = command.WinnerTaskId,
            RescheduledTaskIds = resolvedLosers,
            DecisionReason = "Forced conflict resolved by user: winner " + command.WinnerTaskId
                + " executes now; " + resolvedLosers.Count + " other(s) rescheduled by at least "
                + TaskArbitrator.MinimumRescheduleDelay.TotalMinutes + " minute(s) (Confirming losers cancelled)."
        });

        return Success("The arbitration decision was applied.");
    }

    private void SetPendingArbitration(
        TaskArbitrationResult arbitration,
        IReadOnlyList<TaskInstance> due)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                EngineStatus = SchedulerEngineStatus.Running,
                PendingArbitration = new PendingArbitration
                {
                    CandidateTaskIds = due.Select(instance => instance.SourceTaskId).ToList(),
                    DecisionReason = arbitration.DecisionReason
                },
                LastArbitration = null
            };
        }
    }

    private void SetLastArbitration(TaskArbitrationResult arbitration)
        => SetLastArbitration(new ArbitrationOutcome
        {
            WinnerTaskId = arbitration.WinnerTaskId,
            MergedTaskIds = arbitration.MergedTaskIds,
            RescheduledTaskIds = arbitration.RescheduledTaskIds,
            RequiresUserDecision = arbitration.RequiresUserDecision,
            DecisionReason = arbitration.DecisionReason
        });

    private void SetLastArbitration(ArbitrationOutcome outcome)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                LastArbitration = outcome,
                PendingArbitration = null
            };
        }
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
        // waiting → confirming（瞬时，不落盘中间态）。
        var confirming = _stateMachine.TryTransition(
            instance.State,
            TaskInstanceState.Confirming,
            TaskInstanceStateTransitionCause.ScheduleTriggered,
            SourceName);
        if (!confirming.Allowed)
        {
            return;
        }

        var updated = instance with { State = TaskInstanceState.Confirming };
        await PersistAndCommitInstanceAsync(updated, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// S20-D1 倒计时边界确认裁决。在 Confirming→Running 之前，基于最新实例状态重新裁决：
    /// <list type="number">
    /// <item>取消/终态胜出：实例非 Waiting/Confirming 一律拒绝。</item>
    /// <item>输入恢复胜出：空闲触发且已进入倒计时的实例，到期边界重新检测输入，恢复即取消。</item>
    /// <item>无人值守等效确认：仅对显式选择 UseUnattended 的任务评估；授权异常/失效一律 fail-closed 取消。</item>
    /// </list>
    /// 返回 true 表示可继续（有效人工确认已内置于 RealPowerConfirmed，或无人值守等效确认有效）；
    /// false 表示已拒绝（实例被取消，不进 Pipeline、不调用电源）。不改冻结状态机、双闸门、
    /// 唯一电源出口与 Pre-Pipeline 顺序。
    /// </summary>
    private async Task<bool> ResolveCountdownConfirmationAsync(
        TaskInstance instance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 基于最新实例状态裁决（取消/终态在快照中已反映，绝不信任过期候选副本）。
        var latest = FindInstance(instance.InstanceId) ?? instance;

        if (latest.State is not (TaskInstanceState.Waiting or TaskInstanceState.Confirming))
        {
            return false;
        }

        // 输入恢复胜出：仅空闲触发且已进入倒计时的实例，在到期边界重新检测输入。
        if (latest.State == TaskInstanceState.Confirming
            && latest.IsIdleTriggered
            && _idleMonitor is not null)
        {
            var definition = _taskService.Get(latest.SourceTaskId);
            if (definition is not null)
            {
                var idleDuration = _idleMonitor.GetIdleDuration();
                if (idleDuration is not null
                    && !IdleShutdownRule.IsIdleDue(definition, _globalDefaultIdleThreshold, idleDuration))
                {
                    await CancelIdleTriggeredAsync(latest, now, cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }
        }

        // 无人值守等效确认：仅对显式选择无人值守的任务评估（默认关闭、fail-closed）。
        if (latest.UseUnattended)
        {
            if (_unattendedPolicyService is null || _unattendedEvaluator is null)
            {
                await CancelUnattendedAsync(latest, now, cancellationToken).ConfigureAwait(false);
                return false;
            }

            UnattendedAuthorizationDecision authorization;
            try
            {
                authorization = await _unattendedPolicyService
                    .EvaluateAsync(latest.ActionSnapshot, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await CancelUnattendedAsync(latest, now, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var decision = _unattendedEvaluator.Evaluate(authorization, latest);
            if (!decision.AllowsPower)
            {
                await CancelUnattendedAsync(latest, now, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    /// <summary>无人值守等效确认被拒绝：通过冻结白名单 Confirming→Cancelled 终结，不执行电源。</summary>
    private async Task CancelUnattendedAsync(
        TaskInstance instance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cancelled = _taskService.Cancel(instance);
        if (cancelled.Succeeded && cancelled.Instance is not null)
        {
            await PersistAndCommitInstanceAsync(cancelled.Instance, now, cancellationToken).ConfigureAwait(false);
        }
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

        // S20-D1：倒计时边界确认裁决。基于最新实例状态 + 取消/输入事实 + 有效授权，
        // 在 Confirming→Running 之前完成；取消/输入恢复/终态/授权异常或失效一律拒绝，
        // 不进 Pipeline、不调用电源（仅有效人工确认或有效无人值守等效确认可继续）。
        if (!await ResolveCountdownConfirmationAsync(instance, now, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (instance.State == TaskInstanceState.Waiting)
        {
            // 无告警窗口：waiting → confirming（瞬时，不落盘中间态）。
            var confirming = _stateMachine.TryTransition(
                instance.State,
                TaskInstanceState.Confirming,
                TaskInstanceStateTransitionCause.ScheduleTriggered,
                SourceName);
            if (!confirming.Allowed)
            {
                return;
            }
        }

        // 确认 → Pre-Pipeline（瞬时）→ 电源。
        var pipelineStarted = _stateMachine.TryTransition(
            TaskInstanceState.Confirming,
            TaskInstanceState.Running,
            TaskInstanceStateTransitionCause.PowerConfirmed,
            SourceName);
        if (!pipelineStarted.Allowed)
        {
            return;
        }

        var executingTransition = _stateMachine.TryTransition(
            TaskInstanceState.Running,
            TaskInstanceState.Executing,
            TaskInstanceStateTransitionCause.PipelineCompleted,
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

        var executed = await EnterTerminalAsync(
            executing,
            TaskInstanceState.Executed,
            TaskInstanceStateTransitionCause.PowerCompleted,
            _clock.UtcNow.ToUniversalTime(),
            cancellationToken).ConfigureAwait(false);

        if (executed is not null)
        {
            await TryRescheduleRecurringAsync(executed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TaskInstance?> EnterTerminalAsync(
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
            return null;
        }

        var terminal = instance with { State = target };
        if (!await PersistAndCommitInstanceAsync(terminal, now, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return terminal;
    }

    /// <summary>
    /// 周期规则改期（S14）：执行后为周期规则（DailyAt/Weekdays/NthWorkdayOfMonth）计算下次触发
    /// 并 executed→waiting；一次性/倒计时/下个工作日规则触发后终结（不再重排）。
    /// 无后续触发或损坏规则时任务保持已执行终态，不 SetFaulted 整机、不影响其他任务
    /// （冻结状态机无 executed→faulted 边，故以“保持终态”表达安全终结）。
    /// </summary>
    private async Task TryRescheduleRecurringAsync(
        TaskInstance executed,
        CancellationToken cancellationToken)
    {
        var definition = _taskService.Get(executed.SourceTaskId);
        if (definition is null || !definition.IsEnabled)
        {
            return;
        }

        if (!TaskDefinitionValidator.IsRecurringKind(definition.Kind))
        {
            return;
        }

        var now = _clock.UtcNow.ToUniversalTime();
        var result = _taskService.RescheduleRecurring(
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
            ResolveArbitrationCommand resolve => await HandleResolveArbitrationAsync(resolve, cancellationToken).ConfigureAwait(false),
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

        // S14：登记定义到领域集合（供周期改期/启停）。定义持久化（tasks.json）由应用层
        // 负责（checkpoint 4 的“保存/恢复”），引擎此处仅做内存登记，不触碰运行态写入序列。
        var register = RegisterDefinition(command.Definition);
        if (!register.Succeeded)
        {
            return Rejected(
                GetSnapshot(),
                SchedulerCommandStatus.TaskServiceRejected,
                register.Message);
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

        // 过期一次性任务诞生即 Cancelled（终态），如实告知而非笼统“已创建”。
        return taskResult.Instance!.State == TaskInstanceState.Cancelled
            ? Success(taskResult.Message)
            : Success("The task was created.");
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
            // PendingArbitration / LastArbitration 是瞬态 UI 呈现字段，不持久化；
            // 由 SetPendingArbitration / SetLastArbitration 显式管理，持久化操作携带保留。
            _snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                Instances = instances,
                LastUpdatedAt = now,
                PendingArbitration = _snapshot.PendingArbitration,
                LastArbitration = _snapshot.LastArbitration
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

    /// <summary>登记任务定义到领域集合：新增；已存在则刷新（重创建同一 Id 的场景）。</summary>
    private TaskCollectionResult RegisterDefinition(TaskDefinition definition)
    {
        var add = _taskService.Add(definition);
        return add.Status == TaskCollectionStatus.DuplicateId
            ? _taskService.Update(definition)
            : add;
    }

    private IReadOnlyList<TaskInstance> GetActiveInstances()
        => GetSnapshot().Instances.Values
            .Where(instance => instance.State is TaskInstanceState.Waiting or TaskInstanceState.Confirming)
            .ToList();

    private DateTimeOffset? ComputeNextDeadline(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        var idleToMonitor = false;

        foreach (var instance in GetActiveInstances())
        {
            if (IsIdleInstance(instance))
            {
                idleToMonitor = true;
            }

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

        // Idle 任务的触发/取消依赖外部输入（非墙钟），无法由 deadline 精确表达；
        // 存在待监控的 Idle 实例时，加入短轮询 deadline 以周期性重新评估空闲时长。
        if (idleToMonitor && _idleMonitor is not null)
        {
            var poll = now.Add(IdlePollInterval).ToUniversalTime();
            if (next is null || poll < next.Value)
            {
                next = poll;
            }
        }

        return next;
    }

    private bool IsDueNow(TaskInstance instance, DateTimeOffset now)
    {
        return instance.State switch
        {
            // Idle 任务的 Waiting 占位 fire time 不代表真实触发；仅由空闲评估触发。
            TaskInstanceState.Waiting when IsIdleInstance(instance) => false,
            TaskInstanceState.Waiting => instance.ScheduledFireTime <= now
                || (instance.WarningStartTime is { } warningStart && warningStart <= now),
            TaskInstanceState.Confirming => instance.ScheduledFireTime <= now,
            _ => false
        };
    }

    private DateTimeOffset? ComputeFutureDeadline(TaskInstance instance, DateTimeOffset now)
    {
        return instance.State switch
        {
            // Idle 任务的 Waiting 占位 fire time 不产生墙钟 deadline；由轮询驱动空闲评估。
            TaskInstanceState.Waiting when IsIdleInstance(instance) => null,
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
