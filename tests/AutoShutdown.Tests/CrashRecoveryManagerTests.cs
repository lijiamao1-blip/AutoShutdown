using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Recovery;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S13-T05 崩溃恢复迁出：CrashRecoveryManager 独立单元测试。
/// 覆盖：瞬态实例（running/confirming/executing）→ interrupted 且不补执行；
/// waiting 保留、终态保持；恢复只写一次；审计 taskId 数据；
/// 以及「恢复后的 interrupted 实例不会被调度循环执行」的集成断言。
/// </summary>
public sealed class CrashRecoveryManagerTests
{
    private const string TaskA = "11111111-1111-1111-1111-111111111111";
    private const string TaskB = "22222222-2222-2222-2222-222222222222";
    private const string TaskC = "33333333-3333-3333-3333-333333333333";
    private const string InstanceA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string InstanceB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string InstanceC = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string TokenA = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    private static CrashRecoveryManager CreateManager(InMemoryStorage storage)
        => new(storage, new TaskInstanceStateMachine(), new FakeClock(Now));

    private static string InstanceJson(
        string instanceId,
        string taskId,
        int state,
        bool hasExecuted,
        string? warningStartTime = null)
        => warningStartTime is null
            ? $$"""{"InstanceId":"{{instanceId}}","SourceTaskId":"{{taskId}}","ActionSnapshot":1,"State":{{state}},"ScheduledFireTime":"2024-01-15T12:00:00+00:00","WarningStartTime":null,"StageToken":"{{TokenA}}","HasExecuted":{{(hasExecuted ? "true" : "false")}},"CreatedAt":"2024-01-15T10:00:00+00:00"}"""
            : $$"""{"InstanceId":"{{instanceId}}","SourceTaskId":"{{taskId}}","ActionSnapshot":1,"State":{{state}},"ScheduledFireTime":"2024-01-15T12:00:00+00:00","WarningStartTime":"{{warningStartTime}}","StageToken":"{{TokenA}}","HasExecuted":{{(hasExecuted ? "true" : "false")}},"CreatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string RunningJson =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA}}":{{InstanceJson(InstanceA, TaskA, (int)TaskInstanceState.Running, false)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string ConfirmingJson =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA}}":{{InstanceJson(InstanceA, TaskA, (int)TaskInstanceState.Confirming, false, "2024-01-15T11:59:00+00:00")}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string ExecutingJson =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA}}":{{InstanceJson(InstanceA, TaskA, (int)TaskInstanceState.Executing, true)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string WaitingJson =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskB}}":{{InstanceJson(InstanceB, TaskB, (int)TaskInstanceState.Waiting, false)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string TerminalJson =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskC}}":{{InstanceJson(InstanceC, TaskC, (int)TaskInstanceState.Executed, true)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private static string MixedJson =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA}}":{{InstanceJson(InstanceA, TaskA, (int)TaskInstanceState.Executing, true)}},"{{TaskB}}":{{InstanceJson(InstanceB, TaskB, (int)TaskInstanceState.Waiting, false)}},"{{TaskC}}":{{InstanceJson(InstanceC, TaskC, (int)TaskInstanceState.Executed, true)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    [Theory]
    [InlineData(nameof(RunningJson))]
    [InlineData(nameof(ConfirmingJson))]
    [InlineData(nameof(ExecutingJson))]
    public async Task Recover_TransientInstances_Interrupted_WriteOnce(string seedName)
    {
        var json = seedName switch
        {
            nameof(RunningJson) => RunningJson,
            nameof(ConfirmingJson) => ConfirmingJson,
            _ => ExecutingJson
        };

        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", json);
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        Assert.Equal(CrashRecoveryStatus.Recovered, result.Status);
        Assert.Single(result.InterruptedTaskIds);
        Assert.Equal(Guid.Parse(TaskA), result.InterruptedTaskIds[0]);
        Assert.Equal(1, storage.WriteCount);

        var recovered = result.State!.Instances[Guid.Parse(TaskA)];
        Assert.Equal(TaskInstanceState.Interrupted, recovered.State);
    }

    [Fact]
    public async Task Recover_Executing_PreservesHasExecuted()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", ExecutingJson);
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        var recovered = result.State!.Instances[Guid.Parse(TaskA)];
        Assert.Equal(TaskInstanceState.Interrupted, recovered.State);
        Assert.True(recovered.HasExecuted);
    }

    [Fact]
    public async Task Recover_Waiting_StaysWaiting_NoWrite()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", WaitingJson);
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        Assert.Equal(CrashRecoveryStatus.NoRecoveryNeeded, result.Status);
        Assert.Empty(result.InterruptedTaskIds);
        Assert.Equal(0, storage.WriteCount);
        Assert.Equal(TaskInstanceState.Waiting, result.State!.Instances[Guid.Parse(TaskB)].State);
    }

    [Fact]
    public async Task Recover_Terminal_StaysTerminal_NoWrite()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", TerminalJson);
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        Assert.Equal(CrashRecoveryStatus.NoRecoveryNeeded, result.Status);
        Assert.Empty(result.InterruptedTaskIds);
        Assert.Equal(0, storage.WriteCount);
        Assert.Equal(TaskInstanceState.Executed, result.State!.Instances[Guid.Parse(TaskC)].State);
    }

    [Fact]
    public async Task Recover_MixedInstances_OnlyTransientInterrupted_WriteOnce()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", MixedJson);
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        Assert.Equal(CrashRecoveryStatus.Recovered, result.Status);
        Assert.Single(result.InterruptedTaskIds);
        Assert.Equal(Guid.Parse(TaskA), result.InterruptedTaskIds[0]);
        Assert.Equal(1, storage.WriteCount);

        var state = result.State!;
        Assert.Equal(TaskInstanceState.Interrupted, state.Instances[Guid.Parse(TaskA)].State);
        Assert.Equal(TaskInstanceState.Waiting, state.Instances[Guid.Parse(TaskB)].State);
        Assert.Equal(TaskInstanceState.Executed, state.Instances[Guid.Parse(TaskC)].State);
    }

    [Fact]
    public async Task Recover_NoFile_ReturnsNotFound_NoWrite()
    {
        var storage = new InMemoryStorage();
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        Assert.Equal(CrashRecoveryStatus.NotFound, result.Status);
        Assert.Equal(0, storage.WriteCount);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task Recover_Corrupt_ReturnsCorrupt()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", "{ incomplete");
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        Assert.Equal(CrashRecoveryStatus.Corrupt, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task Recover_Invalid_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", """{"SchemaVersion":2,"Instances":null,"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""");
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        Assert.Equal(CrashRecoveryStatus.Invalid, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task Recover_Result_ContainsInterruptedTaskIds_ForAudit()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", MixedJson);
        var manager = CreateManager(storage);

        var result = await manager.RecoverAsync(CancellationToken.None);

        // 审计 Warning 所需数据：被中断实例的 task id 原样出现在结果中。
        Assert.Contains(Guid.Parse(TaskA), result.InterruptedTaskIds);
        Assert.DoesNotContain(Guid.Parse(TaskB), result.InterruptedTaskIds);
        Assert.DoesNotContain(Guid.Parse(TaskC), result.InterruptedTaskIds);
    }

    [Fact(Timeout = 2000)]
    public async Task RecoveredInstances_AreNotExecutedByScheduler()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", ExecutingJson);
        var manager = CreateManager(storage);

        var recovery = await manager.RecoverAsync(CancellationToken.None);
        Assert.Equal(CrashRecoveryStatus.Recovered, recovery.Status);

        var handler = new CountingHandler();
        var engine = CreateEngine(storage, new ControllableDeadline(), handler);
        using var scope = new EngineScope(engine);

        await WaitUntilAsync(() => engine.GetSnapshot().Instances.Count > 0);
        await Task.Delay(50);

        // interrupted 实例为终态，调度循环绝不补执行。
        Assert.Equal(0, handler.CallCount);
    }

    private static SchedulerEngine CreateEngine(
        IStorage storage,
        ControllableDeadline deadline,
        CountingHandler handler)
    {
        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(Guid.Parse(InstanceA), Guid.Parse(TokenA)));

        return new SchedulerEngine(
            storage,
            new FakeClock(Now),
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(Guid.Parse(TokenA)),
            handler,
            new NoOpTaskArbitrator());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
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
