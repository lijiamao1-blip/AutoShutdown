using System.Diagnostics;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// 错过触发不补执行 + 执行阶段可中止（v2.0.17 用户反馈：手动关机后次日开机被强行关机、
/// 关机流程中停止/停用无效、提醒没有出现）。
/// </summary>
public sealed class MissedFireAndExecutionAbortTests
{
    private static readonly Guid TaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly DateTimeOffset FireTime = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact(Timeout = 5000)]
    public async Task Startup_AfterMissedFire_OneShotTask_IsCancelledAndNeverExecuted()
    {
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, WaitingInstance());
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddHours(21)), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => Current(engine)?.State == TaskInstanceState.Cancelled);

        Assert.Empty(handler.Calls);
        Assert.Equal(SchedulerEngineStatus.Running, engine.GetSnapshot().EngineStatus);
    }

    [Fact(Timeout = 5000)]
    public async Task Startup_AfterMissedFire_DailyTask_IsRescheduledWithFreshReminder()
    {
        var storage = new InMemoryStorage();
        await SeedDefinitionAsync(storage, DailyDefinition());
        await SeedRuntimeAsync(storage, WaitingInstance());
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(new DateTimeOffset(2024, 1, 16, 9, 11, 0, TimeSpan.Zero)), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => Current(engine)?.ScheduledFireTime > FireTime);

        var next = Current(engine)!;
        Assert.Equal(TaskInstanceState.Waiting, next.State);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 12, 0, 0, TimeSpan.Zero), next.ScheduledFireTime);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 11, 50, 0, TimeSpan.Zero), next.WarningStartTime);
        Assert.Empty(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task Startup_SeveralMissedTasks_AreEachSkippedNotMergedByArbitration()
    {
        // 多个任务同时过期：不得进入仲裁「合并」——否则落选的每日任务会被取消而永久停摆。
        var dailyId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var storage = new InMemoryStorage();
        await SeedDefinitionAsync(storage, DailyDefinition() with { Id = dailyId });
        var oneShot = WaitingInstance();
        var daily = WaitingInstance() with
        {
            InstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SourceTaskId = dailyId,
            StageToken = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        };
        var save = await new RuntimeStateStore(storage).SaveAsync(
            new RuntimeState
            {
                Instances = new Dictionary<Guid, TaskInstance> { [TaskId] = oneShot, [dailyId] = daily },
                LastUpdatedAt = oneShot.CreatedAt
            },
            CancellationToken.None);
        Assert.True(save.Succeeded);
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(new DateTimeOffset(2024, 1, 16, 9, 0, 0, TimeSpan.Zero)), handler, productionArbitrator: true);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() =>
            engine.GetSnapshot().Instances.TryGetValue(dailyId, out var d) && d.ScheduledFireTime > FireTime
            && engine.GetSnapshot().Instances[TaskId].State == TaskInstanceState.Cancelled);

        var rescheduled = engine.GetSnapshot().Instances[dailyId];
        Assert.Equal(TaskInstanceState.Waiting, rescheduled.State);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 12, 0, 0, TimeSpan.Zero), rescheduled.ScheduledFireTime);
        Assert.Null(engine.GetSnapshot().LastArbitration);
        Assert.Empty(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task Startup_AfterMissedFireDuringReminder_ConfirmingInstance_IsNotExecuted()
    {
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, WaitingInstance() with { State = TaskInstanceState.Confirming });
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddMinutes(30)), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => Current(engine)?.State == TaskInstanceState.Cancelled);

        Assert.Empty(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task Startup_AfterShutdownDuringReminder_InterruptedDailyTask_IsRearmed()
    {
        var storage = new InMemoryStorage();
        await SeedDefinitionAsync(storage, DailyDefinition());
        await SeedRuntimeAsync(storage, WaitingInstance() with { State = TaskInstanceState.Interrupted });
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(new DateTimeOffset(2024, 1, 16, 9, 0, 0, TimeSpan.Zero)), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => Current(engine)?.State == TaskInstanceState.Waiting);

        Assert.Equal(new DateTimeOffset(2024, 1, 16, 12, 0, 0, TimeSpan.Zero), Current(engine)!.ScheduledFireTime);
        Assert.Empty(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task Startup_InterruptedOneShotTask_StaysInterrupted()
    {
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, WaitingInstance() with { State = TaskInstanceState.Interrupted });
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddHours(21)), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running);
        await Task.Delay(50);

        Assert.Equal(TaskInstanceState.Interrupted, Current(engine)!.State);
        Assert.Empty(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task WakeJustAfterFire_ReminderNeverShown_RearmsFullReminderInsteadOfExecuting()
    {
        // 23:50 提醒、00:00 执行，电脑 23:40 睡眠、00:01 唤醒：提醒从未出现，绝不直接关机。
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, WaitingInstance());
        var handler = new RecordingHandler();
        var now = FireTime.AddMinutes(1);
        var engine = CreateEngine(storage, new FakeClock(now), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => Current(engine)?.State == TaskInstanceState.Confirming);

        var reminded = Current(engine)!;
        Assert.Equal(now.AddMinutes(10), reminded.ScheduledFireTime);
        Assert.NotEqual(StageToken, reminded.StageToken);
        Assert.Empty(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task SnoozeFromReminderWindow_PostponesFromOriginalTimeAndRemindsAgain()
    {
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, ReminderShownInstance());
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddMinutes(-5)), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => Current(engine)?.State == TaskInstanceState.Confirming);

        var result = await engine.SubmitAsync(
            new SnoozeTaskCommand(InstanceId, StageToken, TimeSpan.FromMinutes(10)),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var snoozed = Current(engine)!;
        Assert.Equal(TaskInstanceState.Waiting, snoozed.State);
        Assert.Equal(FireTime.AddMinutes(10), snoozed.ScheduledFireTime);
        Assert.Equal(FireTime, snoozed.WarningStartTime);
        Assert.Empty(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task DueWithinTolerance_StillExecutesOnce()
    {
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, ReminderShownInstance());
        var handler = new RecordingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddMinutes(1)), handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => Current(engine)?.State == TaskInstanceState.Executed);

        Assert.Single(handler.Calls);
    }

    [Fact(Timeout = 5000)]
    public async Task StopDuringExecution_AbortsBeforePowerAndReportsSuccess()
    {
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, ReminderShownInstance());
        var handler = new BlockingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddSeconds(1)), handler);

        using var scope = new EngineScope(engine);
        await handler.Started.Task;
        Assert.Equal(TaskInstanceState.Executing, Current(engine)!.State);

        var result = await engine.SubmitAsync(new CancelTaskCommand(InstanceId, StageToken), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(handler.ObservedCancellation);
        Assert.False(handler.ReachedPower);
        var stopped = Current(engine)!;
        Assert.Equal(TaskInstanceState.Cancelled, stopped.State);
        Assert.Equal(SchedulerEngineStatus.Running, engine.GetSnapshot().EngineStatus);
    }

    [Fact(Timeout = 5000)]
    public async Task DisableDuringExecution_DailyTask_AbortsAndKeepsNextOccurrenceDisabled()
    {
        var storage = new InMemoryStorage();
        await SeedDefinitionAsync(storage, DailyDefinition());
        await SeedRuntimeAsync(storage, ReminderShownInstance());
        var handler = new BlockingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddSeconds(1)), handler);

        using var scope = new EngineScope(engine);
        await handler.Started.Task;

        var result = await engine.SubmitAsync(new SetTaskEnabledCommand(TaskId, false), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(handler.ReachedPower);
        var next = Current(engine)!;
        Assert.Equal(TaskInstanceState.Waiting, next.State);
        Assert.False(next.IsEnabled);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 12, 0, 0, TimeSpan.Zero), next.ScheduledFireTime);
    }

    [Fact(Timeout = 5000)]
    public async Task UnrelatedCommandDuringExecution_DoesNotAbort()
    {
        var storage = new InMemoryStorage();
        await SeedRuntimeAsync(storage, ReminderShownInstance());
        var handler = new BlockingHandler();
        var engine = CreateEngine(storage, new FakeClock(FireTime.AddSeconds(1)), handler);

        using var scope = new EngineScope(engine);
        await handler.Started.Task;

        var pending = engine.SubmitAsync(new CancelTaskCommand(InstanceId, Guid.NewGuid()), CancellationToken.None);
        await Task.Delay(100);

        Assert.False(handler.ObservedCancellation);
        handler.Release();
        await WaitUntilAsync(() => Current(engine)?.State == TaskInstanceState.Executed);
        Assert.Equal(SchedulerCommandStatus.StaleCommand, (await pending).Status);
    }

    private static TaskInstance WaitingInstance() => new()
    {
        InstanceId = InstanceId,
        SourceTaskId = TaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Waiting,
        ScheduledFireTime = FireTime,
        WarningStartTime = FireTime.AddMinutes(-10),
        StageToken = StageToken,
        CreatedAt = FireTime.AddHours(-2),
        RealPowerConfirmed = true,
        IsEnabled = true
    };

    /// <summary>提醒已显示（Confirming）的实例：到点后正常进入执行。</summary>
    private static TaskInstance ReminderShownInstance() => WaitingInstance() with { State = TaskInstanceState.Confirming };

    private static TaskDefinition DailyDefinition() => new()
    {
        Id = TaskId,
        Kind = TaskKind.DailyAt,
        Action = PowerAction.Shutdown,
        TargetTimeOfDay = new TimeOnly(12, 0),
        WarningSeconds = 600,
        CreatedAt = FireTime.AddDays(-1),
        RealPowerConfirmed = true
    };

    private static async Task SeedRuntimeAsync(IStorage storage, TaskInstance instance)
    {
        var save = await new RuntimeStateStore(storage).SaveAsync(
            new RuntimeState
            {
                Instances = new Dictionary<Guid, TaskInstance> { [instance.SourceTaskId] = instance },
                LastUpdatedAt = instance.CreatedAt
            },
            CancellationToken.None);
        Assert.True(save.Succeeded);
    }

    private static async Task SeedDefinitionAsync(IStorage storage, TaskDefinition definition)
    {
        var save = await new TasksDocumentStore(storage).SaveAsync(
            new TasksDocument { Tasks = [definition] },
            CancellationToken.None);
        Assert.True(save.Succeeded);
    }

    private static SchedulerEngine CreateEngine(
        IStorage storage,
        IClock clock,
        IScheduledTaskHandler handler,
        bool productionArbitrator = false)
    {
        var taskService = new TaskService(new NextExecutionCalculator(), new TaskInstanceStateMachine(), new GuidIdentifierGenerator());
        return new SchedulerEngine(
            storage,
            clock,
            new NeverDeadline(),
            taskService,
            new TaskInstanceStateMachine(),
            new GuidIdentifierGenerator(),
            handler,
            productionArbitrator ? new TaskArbitrator(taskService) : new NoOpTaskArbitrator());
    }

    private static TaskInstance? Current(SchedulerEngine engine)
        => engine.GetSnapshot().Instances.Values.FirstOrDefault();

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for the condition.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class NeverDeadline : IAsyncDeadline
    {
        public Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class RecordingHandler : IScheduledTaskHandler
    {
        public List<TaskInstance> Calls { get; } = new();

        public Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            Calls.Add(instance);
            return Task.CompletedTask;
        }
    }

    /// <summary>模拟耗时的关机前流程（保存文档/关闭应用）：阻塞直到被取消或放行。</summary>
    private sealed class BlockingHandler : IScheduledTaskHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedCancellation { get; private set; }

        public bool ReachedPower { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }

            ReachedPower = true;
        }
    }

    private sealed class EngineScope : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public EngineScope(SchedulerEngine engine) => _run = engine.RunAsync(_cts.Token);

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _run.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, string> _documents = new();
        private readonly object _gate = new();

        public Task<StorageReadResult<T>> ReadAsync<T>(string relativePath, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(_documents.TryGetValue(relativePath, out var json)
                    ? new StorageReadResult<T> { Status = StorageReadStatus.Success, Value = JsonSerializer.Deserialize<T>(json) }
                    : new StorageReadResult<T> { Status = StorageReadStatus.NotFound });
            }
        }

        public Task<StorageWriteResult> WriteAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _documents[relativePath] = JsonSerializer.Serialize(value);
            }

            return Task.FromResult(new StorageWriteResult { Status = StorageWriteStatus.Success });
        }
    }
}
