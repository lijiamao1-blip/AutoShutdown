using System.Diagnostics;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S15 检查点 2：调度器空闲接入。空闲评估（阈值达成触发）、输入恢复取消、
/// 到期仲裁（多个空闲任务同刻到期进入统一仲裁），以及非 Idle 任务不受影响。
/// 全程使用替身电源（NoOp 仲裁 + FakeHandler），绝不触发真实电源。
/// </summary>
public sealed class S15_SchedulerIdleIntegrationTests
{
    private static readonly DateTimeOffset ClockBase = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid IdleId1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid IdleId2 = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid CountdownId = Guid.Parse("10000000-0000-0000-0000-000000000003");

    [Fact(Timeout = 2000)]
    public async Task IdleTask_WhenIdleReachesThreshold_ArmsToConfirming()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);
        Assert.True(created.Succeeded);

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Confirming, instance.State);
        Assert.True(instance.IsIdleTriggered);
        Assert.Equal(ClockBase.AddSeconds(60), instance.ScheduledFireTime);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task IdleTask_BelowThreshold_StaysWaiting()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(4) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: 300)),
            CancellationToken.None);

        // 空闲时长低于阈值：不应触发；轮询 deadline 会周期唤醒，但状态保持 Waiting。
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();
        await Task.Delay(50);

        Assert.Equal(TaskInstanceState.Waiting, Current(engine.GetSnapshot())!.State);
        Assert.False(Current(engine.GetSnapshot())!.IsIdleTriggered);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task IdleTask_DetectionFailure_StaysWaiting()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = null };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: 300)),
            CancellationToken.None);

        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();
        await Task.Delay(50);

        Assert.Equal(TaskInstanceState.Waiting, Current(engine.GetSnapshot())!.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task IdleTask_WithoutOwnThreshold_InheritsGlobalDefault()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(11) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(
            storage, clock, deadline, idle, handler,
            globalDefaultIdleThreshold: TimeSpan.FromMinutes(10));

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: null, warningSeconds: 60)),
            CancellationToken.None);

        // 任务自身未指定阈值 → 继承全局默认 10 分钟；空闲 11 分钟 ≥ 阈值 → 触发。
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        Assert.True(Current(engine.GetSnapshot())!.IsIdleTriggered);
    }

    [Fact(Timeout = 2000)]
    public async Task IdleTask_WithNoWarning_ExecutesAfterArming()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: 300)),
            CancellationToken.None);

        await WaitUntilAsync(() => handler.CallCount == 1);

        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Executed, instance.State);
        Assert.True(instance.HasExecuted);
        Assert.True(instance.IsIdleTriggered);
    }

    [Fact(Timeout = 2000)]
    public async Task IdleTask_WithWarning_ExecutesAfterCountdownElapses()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        // 倒计时结束：推进时钟到触发时刻并唤醒。
        clock.UtcNow = ClockBase.AddSeconds(60);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();

        await WaitUntilAsync(() => handler.CallCount == 1);
        Assert.Equal(TaskInstanceState.Executed, Current(engine.GetSnapshot())!.State);
    }

    [Fact(Timeout = 2000)]
    public async Task IdleTask_InputRecoveryDuringCountdown_Cancels()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        // 输入恢复：空闲时长回落到阈值以下 → 取消倒计时，不执行电源。
        idle.IdleDuration = TimeSpan.Zero;
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Cancelled);

        Assert.Equal(TaskInstanceState.Cancelled, Current(engine.GetSnapshot())!.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task NonIdleTask_UnaffectedByIdleMonitor()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        var countdown = new TaskDefinition
        {
            Id = CountdownId,
            Kind = TaskKind.Countdown,
            Action = PowerAction.Shutdown,
            CountdownDuration = TimeSpan.FromHours(2),
            WarningSeconds = 60,
            CreatedAt = ClockBase
        };

        await engine.SubmitAsync(new CreateTaskCommand(countdown), CancellationToken.None);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = ClockBase.AddHours(2).AddSeconds(-60);
        deadline.CompleteNext();
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        clock.UtcNow = ClockBase.AddHours(2);
        deadline.CompleteNext();
        await WaitUntilAsync(() => handler.CallCount == 1);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(TaskInstanceState.Executed, Current(engine.GetSnapshot())!.State);
    }

    [Fact(Timeout = 2000)]
    public async Task TwoIdleTasks_DueSimultaneously_EnterUnifiedArbitration()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var handler = new FakeHandler();
        var engine = CreateIdleEngine(storage, clock, deadline, idle, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId1, idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(IdleId2, idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);

        // 两个空闲任务同时触发并进入倒计时；推进时钟让二者同刻到期。
        await WaitUntilAsync(() => engine.GetSnapshot().Instances.Values
            .All(instance => instance.State == TaskInstanceState.Confirming && instance.IsIdleTriggered));

        clock.UtcNow = ClockBase.AddSeconds(60);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();

        // 同刻到期 → 统一仲裁（不并发绕过仲裁直接执行电源）。
        await WaitUntilAsync(() => engine.GetSnapshot().PendingArbitration is not null);

        var pending = engine.GetSnapshot().PendingArbitration!;
        Assert.Equal(2, pending.CandidateTaskIds.Count);
        Assert.Equal(0, handler.CallCount);
    }

    private static SchedulerEngine CreateIdleEngine(
        IStorage storage,
        FakeClock clock,
        ControllableDeadline deadline,
        StubIdleMonitor idleMonitor,
        FakeHandler handler,
        TimeSpan? globalDefaultIdleThreshold = null)
    {
        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new CountingIdentifierGenerator());

        return new SchedulerEngine(
            storage,
            clock,
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            new CountingIdentifierGenerator(),
            handler,
            new NoOpTaskArbitrator(),
            idleMonitor,
            globalDefaultIdleThreshold);
    }

    private static TaskDefinition IdleDefinition(
        Guid id,
        int? idleThresholdSeconds,
        int? warningSeconds = null) => new()
        {
            Id = id,
            Kind = TaskKind.Idle,
            Action = PowerAction.Shutdown,
            IdleThresholdSeconds = idleThresholdSeconds,
            WarningSeconds = warningSeconds,
            CreatedAt = ClockBase
        };

    private static TaskInstance? Current(SchedulerSnapshot snapshot)
        => snapshot.Instances.Values.FirstOrDefault();

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
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

    private sealed class StubIdleMonitor : IIdleMonitor
    {
        public TimeSpan? IdleDuration { get; set; }

        public bool IsMonitoring => true;

        public void Start() { }

        public void Stop() { }

        public TimeSpan? GetIdleDuration() => IdleDuration;
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class ControllableDeadline : IAsyncDeadline
    {
        private readonly List<TaskCompletionSource> _waiters = new();
        private readonly object _gate = new();

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        public Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiters.Add(tcs);
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => tcs.TrySetCanceled());
            }

            return tcs.Task;
        }

        public void CompleteNext()
        {
            TaskCompletionSource? next = null;
            lock (_gate)
            {
                while (_waiters.Count > 0)
                {
                    next = _waiters[0];
                    _waiters.RemoveAt(0);
                    if (!next.Task.IsCompleted)
                    {
                        break;
                    }

                    next = null;
                }
            }

            next?.TrySetResult();
        }
    }

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, string> _documents = new();

        public void Seed(string relativePath, string json) => _documents[relativePath] = json;

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!_documents.TryGetValue(relativePath, out var json))
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.NotFound
                });
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(json);
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Success,
                    Value = value
                });
            }
            catch (JsonException)
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Corrupt,
                    Error = "Corrupt JSON."
                });
            }
        }

        public Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
        {
            _documents[relativePath] = JsonSerializer.Serialize(value);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
        }
    }

    private sealed class FakeHandler : IScheduledTaskHandler
    {
        public int CallCount { get; private set; }

        public Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingIdentifierGenerator : IIdentifierGenerator
    {
        private int _counter;

        public Guid NewId()
        {
            var value = System.Threading.Interlocked.Increment(ref _counter);
            return Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
        }
    }

    private sealed class EngineScope : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public EngineScope(SchedulerEngine engine)
        {
            RunTask = engine.RunAsync(_cts.Token);
        }

        public Task RunTask { get; }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                RunTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
