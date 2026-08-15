using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S13-T09 仲裁消费方测试：SchedulerEngine 消费 TaskArbitrator 决策
/// （赢家执行 + 落选改期/合并取消 + 强制冲突挂起 + ResolveArbitrationCommand 决策应用），
/// 以及 TaskService.RescheduleAfterArbitration 字段级改期单元测试。
/// 全部使用计数替身处理器，不触发任何真实电源调用。
/// </summary>
public sealed class S13_T09_ArbitrationConsumptionTests
{
    private const string TaskA = "11111111-1111-1111-1111-111111111111";
    private const string TaskB = "22222222-2222-2222-2222-222222222222";
    private const string TaskC = "33333333-3333-3333-3333-333333333333";
    private const string InstanceA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string InstanceB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string TokenA = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string TokenB = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    private static readonly Guid NewToken1 = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid NewToken2 = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FireTime = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ---- 引擎级仲裁消费 ----

    [Fact(Timeout = 4000)]
    public async Task MultipleDue_DifferentActions_WinnerExecutes_LoserRescheduledAtLeastFiveMinutes()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", TwoDueJson());
        var handler = new CountingHandler();
        var engine = CreateEngine(storage, handler);

        using var scope = new EngineScope(engine);

        await WaitUntilAsync(() => handler.CallCount == 1);
        await WaitUntilAsync(() =>
            engine.GetSnapshot().Instances[Guid.Parse(TaskB)].State == TaskInstanceState.Executed);

        var snapshot = engine.GetSnapshot();
        Assert.Equal(SchedulerEngineStatus.Running, snapshot.EngineStatus);
        Assert.Null(snapshot.FaultMessage);

        // 赢家（Shutdown）执行。
        var winner = snapshot.Instances[Guid.Parse(TaskB)];
        Assert.Equal(TaskInstanceState.Executed, winner.State);
        Assert.True(winner.HasExecuted);

        // 落选（Sleep）改期 ≥5 分钟，Waiting 保持，StageToken 刷新。
        var loser = snapshot.Instances[Guid.Parse(TaskA)];
        Assert.Equal(TaskInstanceState.Waiting, loser.State);
        Assert.True(loser.ScheduledFireTime >= FireTime.AddMinutes(5), "loser must be rescheduled by at least 5 minutes");
        Assert.NotEqual(Guid.Parse(TokenA), loser.StageToken);

        // 仲裁结果呈现。
        Assert.Equal(Guid.Parse(TaskB), snapshot.LastArbitration!.WinnerTaskId);
        Assert.Equal(new[] { Guid.Parse(TaskA) }, snapshot.LastArbitration.RescheduledTaskIds);
        Assert.False(snapshot.LastArbitration.RequiresUserDecision);
    }

    [Fact(Timeout = 4000)]
    public async Task MultipleDue_SameAction_MergedLoserCancelled_HandlerCalledOnce()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", SameActionJson());
        var handler = new CountingHandler();
        var engine = CreateEngine(storage, handler);

        using var scope = new EngineScope(engine);

        await WaitUntilAsync(() => handler.CallCount == 1);
        await WaitUntilAsync(() =>
            engine.GetSnapshot().Instances[Guid.Parse(TaskA)].State == TaskInstanceState.Executed);

        var snapshot = engine.GetSnapshot();
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(TaskInstanceState.Executed, snapshot.Instances[Guid.Parse(TaskA)].State);
        Assert.Equal(TaskInstanceState.Cancelled, snapshot.Instances[Guid.Parse(TaskB)].State);
        Assert.Equal(Guid.Parse(TaskA), snapshot.LastArbitration!.WinnerTaskId);
        Assert.Equal(new[] { Guid.Parse(TaskB) }, snapshot.LastArbitration.MergedTaskIds);
        Assert.Empty(snapshot.LastArbitration.RescheduledTaskIds);
    }

    [Fact(Timeout = 4000)]
    public async Task ForcedConflict_SetsPendingArbitration_NoPower_NoFault()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", TwoDueJson());
        var handler = new CountingHandler();
        var engine = CreateEngine(
            storage,
            handler,
            forcedTaskIds: new HashSet<Guid> { Guid.Parse(TaskB) });

        using var scope = new EngineScope(engine);

        await WaitUntilAsync(() => engine.GetSnapshot().PendingArbitration is not null);

        var snapshot = engine.GetSnapshot();
        Assert.Equal(SchedulerEngineStatus.Running, snapshot.EngineStatus);
        Assert.Null(snapshot.FaultMessage);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(2, snapshot.PendingArbitration!.CandidateTaskIds.Count);
        Assert.Contains(Guid.Parse(TaskA), snapshot.PendingArbitration.CandidateTaskIds);
        Assert.Contains(Guid.Parse(TaskB), snapshot.PendingArbitration.CandidateTaskIds);
        Assert.Null(snapshot.LastArbitration);
    }

    [Fact(Timeout = 4000)]
    public async Task ResolveArbitration_WinnerExecutes_LoserRescheduled_PendingCleared()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", TwoDueJson());
        var handler = new CountingHandler();
        var engine = CreateEngine(
            storage,
            handler,
            forcedTaskIds: new HashSet<Guid> { Guid.Parse(TaskB) });

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => engine.GetSnapshot().PendingArbitration is not null);

        var result = await engine.SubmitAsync(
            new ResolveArbitrationCommand(Guid.Parse(TaskA)),
            CancellationToken.None);

        Assert.True(result.Succeeded);

        await WaitUntilAsync(() => handler.CallCount == 1);
        await WaitUntilAsync(() =>
            engine.GetSnapshot().Instances[Guid.Parse(TaskA)].State == TaskInstanceState.Executed);

        var snapshot = engine.GetSnapshot();
        Assert.Null(snapshot.PendingArbitration);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(TaskInstanceState.Executed, snapshot.Instances[Guid.Parse(TaskA)].State);

        var loser = snapshot.Instances[Guid.Parse(TaskB)];
        Assert.Equal(TaskInstanceState.Waiting, loser.State);
        Assert.True(loser.ScheduledFireTime >= FireTime.AddMinutes(5));
        Assert.NotEqual(Guid.Parse(TokenB), loser.StageToken);

        Assert.Equal(Guid.Parse(TaskA), snapshot.LastArbitration!.WinnerTaskId);
        Assert.Contains(Guid.Parse(TaskB), snapshot.LastArbitration.RescheduledTaskIds);
    }

    [Fact(Timeout = 4000)]
    public async Task ResolveArbitration_NoPending_Rejected()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", SingleDueJson());
        var handler = new CountingHandler();
        var engine = CreateEngine(storage, handler);

        using var scope = new EngineScope(engine);
        // 单个到期实例直接执行，不进入仲裁。
        await WaitUntilAsync(() =>
            engine.GetSnapshot().Instances[Guid.Parse(TaskA)].State == TaskInstanceState.Executed);

        var result = await engine.SubmitAsync(
            new ResolveArbitrationCommand(Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SchedulerCommandStatus.InvalidCommand, result.Status);
        Assert.Null(engine.GetSnapshot().PendingArbitration);
    }

    [Fact(Timeout = 4000)]
    public async Task ResolveArbitration_InvalidWinner_Rejected_PendingUnchanged()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", TwoDueJson());
        var handler = new CountingHandler();
        var engine = CreateEngine(
            storage,
            handler,
            forcedTaskIds: new HashSet<Guid> { Guid.Parse(TaskB) });

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => engine.GetSnapshot().PendingArbitration is not null);

        var result = await engine.SubmitAsync(
            new ResolveArbitrationCommand(Guid.Parse(TaskC)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SchedulerCommandStatus.InvalidCommand, result.Status);
        var pending = engine.GetSnapshot().PendingArbitration;
        Assert.NotNull(pending);
        Assert.Equal(2, pending!.CandidateTaskIds.Count);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- TaskService.RescheduleAfterArbitration 单元 ----

    [Fact]
    public void RescheduleAfterArbitration_Waiting_ReschedulesByDelay_RefreshesToken()
    {
        var service = CreateTaskService();
        var current = WaitingInstance();

        var result = service.RescheduleAfterArbitration(current, TaskArbitrator.MinimumRescheduleDelay, Now);

        Assert.True(result.Succeeded);
        var rescheduled = result.Instance!;
        Assert.Equal(TaskInstanceState.Waiting, rescheduled.State);
        Assert.Equal(Now.AddMinutes(5), rescheduled.ScheduledFireTime);
        Assert.Null(rescheduled.WarningStartTime);
        Assert.NotEqual(current.StageToken, rescheduled.StageToken);
        Assert.False(rescheduled.HasExecuted);
    }

    [Fact]
    public void RescheduleAfterArbitration_DelayBelowMinimum_Rejected()
    {
        var service = CreateTaskService();

        var result = service.RescheduleAfterArbitration(
            WaitingInstance(),
            TimeSpan.FromMinutes(4),
            Now);

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCommandStatus.InvalidDuration, result.Status);
    }

    [Fact]
    public void RescheduleAfterArbitration_Confirming_Rejected()
    {
        var service = CreateTaskService();
        var confirming = WaitingInstance() with { State = TaskInstanceState.Confirming };

        var result = service.RescheduleAfterArbitration(confirming, TaskArbitrator.MinimumRescheduleDelay, Now);

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCommandStatus.TransitionRejected, result.Status);
    }

    // ---- 夹具 ----

    private static SchedulerEngine CreateEngine(
        IStorage storage,
        CountingHandler handler,
        IReadOnlySet<Guid>? forcedTaskIds = null)
    {
        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(NewToken1, NewToken2));

        var arbitrator = new TaskArbitrator(taskService, forcedTaskIds);

        return new SchedulerEngine(
            storage,
            new FakeClock(Now),
            new ControllableDeadline(),
            taskService,
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(NewToken1, NewToken2),
            handler,
            arbitrator);
    }

    private static TaskService CreateTaskService()
        => new(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(NewToken1, NewToken2));

    private static TaskInstance WaitingInstance() => new()
    {
        InstanceId = Guid.Parse(InstanceA),
        SourceTaskId = Guid.Parse(TaskA),
        ActionSnapshot = PowerAction.Sleep,
        State = TaskInstanceState.Waiting,
        ScheduledFireTime = Now,
        WarningStartTime = null,
        StageToken = Guid.Parse(TokenA),
        HasExecuted = false,
        CreatedAt = Now,
        RealPowerConfirmed = false
    };

    private static string InstanceJson(string instanceId, string taskId, int action, string stageToken) =>
        $$"""{"InstanceId":"{{instanceId}}","SourceTaskId":"{{taskId}}","ActionSnapshot":{{action}},"State":1,"ScheduledFireTime":"2024-01-15T12:00:00+00:00","WarningStartTime":null,"StageToken":"{{stageToken}}","HasExecuted":false,"CreatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string TwoDueJson() =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA}}":{{InstanceJson(InstanceA, TaskA, (int)PowerAction.Sleep, TokenA)}},"{{TaskB}}":{{InstanceJson(InstanceB, TaskB, (int)PowerAction.Shutdown, TokenB)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string SingleDueJson() =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA}}":{{InstanceJson(InstanceA, TaskA, (int)PowerAction.Sleep, TokenA)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string SameActionJson() =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA}}":{{InstanceJson(InstanceA, TaskA, (int)PowerAction.Shutdown, TokenA)}},"{{TaskB}}":{{InstanceJson(InstanceB, TaskB, (int)PowerAction.Shutdown, TokenB)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for the condition.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class ControllableDeadline : IAsyncDeadline
    {
        public Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken)
            => Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
    }

    private sealed class CountingHandler : IScheduledTaskHandler
    {
        public int CallCount { get; private set; }

        public Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class SequentialIdentifierGenerator : IIdentifierGenerator
    {
        private readonly Queue<Guid> _values;

        public SequentialIdentifierGenerator(params Guid[] values) => _values = new Queue<Guid>(values);

        public Guid NewId()
        {
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("No more identifiers configured.");
            }

            return _values.Dequeue();
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

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, string> _documents = new();
        private int _writeCount;

        public int WriteCount => _writeCount;

        public void Seed(string relativePath, string json) => _documents[relativePath] = json;

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!_documents.TryGetValue(relativePath, out var json))
            {
                return Task.FromResult(new StorageReadResult<T> { Status = StorageReadStatus.NotFound });
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
            _writeCount++;
            _documents[relativePath] = JsonSerializer.Serialize(value);
            return Task.FromResult(new StorageWriteResult { Status = StorageWriteStatus.Success });
        }
    }
}
