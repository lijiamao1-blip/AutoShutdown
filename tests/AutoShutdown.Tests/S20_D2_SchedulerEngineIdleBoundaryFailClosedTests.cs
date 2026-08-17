using System.Diagnostics;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Unattended;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S20-D2 空闲触发任务倒计时边界的 fail-closed 真实 SchedulerEngine 集成测试。
/// 在 Confirming 到期边界，对 IsIdleTriggered=true 的任务：输入状态不明（idleMonitor 缺失、
/// 任务定义缺失、GetIdleDuration()==null）或输入已恢复一律拒绝并安全取消，不进入
/// Confirming→Running、不调用 Handler/Pre-Pipeline/电源；输入未知不得误记为「用户已恢复输入」
/// （IsIdleRecovered 保持 false）。同时回归：既有输入恢复路径（IsIdleRecovered=true）与
/// 有效空闲继续路径（仍空闲 + 有效无人值守 → 仅一次执行）。
/// </summary>
public sealed class S20_D2_SchedulerEngineIdleBoundaryFailClosedTests
{
    private static readonly DateTimeOffset ClockBase = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid SourceTaskId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid InstanceId1 = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid StageToken1 = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // ---- (1) GetIdleDuration()==null（输入状态未知）：取消，Pipeline=0、电源=0 ----

    [Fact(Timeout = 2000)]
    public async Task IdleConfirmingExpiry_InputUnknown_GetIdleDurationNull_FailClosedCancel_NoPipelineNoPower()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var power = new RecordingPowerService(AcceptedResult());
        var (engine, handler) = CreateWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), idle);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);

        // 空闲触发 → Confirming（倒计时 60s）。
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);
        Assert.True(Current(engine.GetSnapshot())!.IsIdleTriggered);

        // 到期边界前输入状态变为未知（如 Win32IdleInputSource.GetLastInputInfo 失败返回 null）。
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        idle.IdleDuration = null;
        clock.UtcNow = ClockBase.AddSeconds(60);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Cancelled);

        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Cancelled, instance.State);
        // 输入未知不是「用户已恢复输入」：不得标记 IsIdleRecovered。
        Assert.False(instance.IsIdleRecovered);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- (2a) _idleMonitor==null（崩溃恢复后无监视器）：取消，Pipeline=0、电源=0 ----

    [Fact(Timeout = 2000)]
    public async Task IdleConfirmingExpiry_NoIdleMonitor_FailClosedCancel_NoPipelineNoPower()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new RecordingPowerService(AcceptedResult());
        // 持久化中已存在 Confirming+IsIdleTriggered 实例，但引擎未装配空闲监视器
        // （崩溃恢复/配置变更场景）→ 到期边界必须 fail-closed，不得继续执行。
        await SeedConfirmingIdleInstanceAsync(storage);

        var (engine, handler) = CreateWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), idleMonitor: null);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = ClockBase.AddHours(1);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Cancelled);

        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Cancelled, instance.State);
        Assert.False(instance.IsIdleRecovered);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- (2b) 任务定义缺失：取消，Pipeline=0、电源=0 ----

    [Fact(Timeout = 2000)]
    public async Task IdleConfirmingExpiry_DefinitionMissing_FailClosedCancel_NoPipelineNoPower()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        // 输入仍空闲（5min >= 阈值），但任务定义缺失（tasks.json NotFound，未登记领域集合）。
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var power = new RecordingPowerService(AcceptedResult());
        await SeedConfirmingIdleInstanceAsync(storage);

        var (engine, handler) = CreateWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), idle);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = ClockBase.AddHours(1);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Cancelled);

        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Cancelled, instance.State);
        Assert.False(instance.IsIdleRecovered);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- (3a) 既有输入恢复路径回归：输入恢复胜出，IsIdleRecovered=true，Pipeline=0、电源=0 ----

    [Fact(Timeout = 2000)]
    public async Task IdleConfirmingExpiry_InputRecovered_FailClosedCancel_NoPipelineNoPower()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var power = new RecordingPowerService(AcceptedResult());
        var (engine, handler) = CreateWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), idle);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);
        Assert.True(Current(engine.GetSnapshot())!.IsIdleTriggered);

        // 到期边界前输入恢复（空闲时长回落到阈值以下）。
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        idle.IdleDuration = TimeSpan.Zero;
        clock.UtcNow = ClockBase.AddSeconds(60);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Cancelled);

        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Cancelled, instance.State);
        // 真实输入恢复才标记 IsIdleRecovered（审计语义不变）。
        Assert.True(instance.IsIdleRecovered);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- (3b) 有效空闲继续路径回归：仍空闲 + 有效无人值守 → 仅一次进入 Pipeline/电源 ----

    [Fact(Timeout = 2000)]
    public async Task IdleConfirmingExpiry_StillIdle_ValidUnattended_ContinuesExactlyOnce()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        var (engine, handler) = CreateWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), idle, policy);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(idleThresholdSeconds: 300, warningSeconds: 60, useUnattended: true)),
            CancellationToken.None);

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);
        Assert.True(Current(engine.GetSnapshot())!.IsIdleTriggered);

        // 到期边界输入仍空闲（>= 阈值），且无人值守授权有效 → 继续执行（仅一次）。
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        clock.UtcNow = ClockBase.AddSeconds(60);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Executed);

        Assert.Equal(1, handler.CallCount);
        Assert.Single(power.Requests);
        Assert.Equal(TaskInstanceState.Executed, Current(engine.GetSnapshot())!.State);
    }

    // ---- 夹具 ----

    private static (SchedulerEngine Engine, CountingHandler Handler) CreateWorkflowEngine(
        IStorage storage,
        FakeClock clock,
        ControllableDeadline deadline,
        IPowerService power,
        AppConfig config,
        StubIdleMonitor? idleMonitor,
        IUnattendedPolicyService? unattendedPolicyService = null)
    {
        var evaluator = new UnattendedConfirmationEvaluator();
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(config),
            power,
            unattendedPolicyService: unattendedPolicyService,
            unattendedEvaluator: evaluator);
        var handler = new CountingHandler(new ShutdownScheduledTaskHandler(workflow));

        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new CountingIdentifierGenerator());

        var engine = new SchedulerEngine(
            storage,
            clock,
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            new CountingIdentifierGenerator(),
            handler,
            new NoOpTaskArbitrator(),
            idleMonitor,
            IdleShutdownRule.GlobalDefaultThreshold,
            unattendedPolicyService,
            evaluator);

        return (engine, handler);
    }

    /// <summary>预置持久化运行态：一个 Confirming+IsIdleTriggered 的空闲触发实例（崩溃恢复场景）。</summary>
    private static async Task SeedConfirmingIdleInstanceAsync(InMemoryStorage storage)
    {
        await storage.WriteAsync(
            RuntimeStateStore.FileName,
            new RuntimeState
            {
                SchemaVersion = RuntimeState.CurrentSchemaVersion,
                Instances = new Dictionary<Guid, TaskInstance>
                {
                    [SourceTaskId] = new TaskInstance
                    {
                        InstanceId = InstanceId1,
                        SourceTaskId = SourceTaskId,
                        ActionSnapshot = PowerAction.Shutdown,
                        State = TaskInstanceState.Confirming,
                        ScheduledFireTime = ClockBase.AddHours(1),
                        StageToken = StageToken1,
                        HasExecuted = false,
                        CreatedAt = ClockBase,
                        IsIdleTriggered = true
                    }
                },
                LastUpdatedAt = ClockBase
            },
            CancellationToken.None);
    }

    private static AppConfig RealPowerConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskDefinition IdleDefinition(
        int? idleThresholdSeconds,
        int? warningSeconds = null,
        bool useUnattended = false) => new()
        {
            Id = SourceTaskId,
            Kind = TaskKind.Idle,
            Action = PowerAction.Shutdown,
            IdleThresholdSeconds = idleThresholdSeconds,
            WarningSeconds = warningSeconds,
            CreatedAt = ClockBase,
            UseUnattended = useUnattended
        };

    private static UnattendedAuthorizationDecision Authorized(PowerAction action)
        => UnattendedAuthorizationDecision.Granted(new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 3,
            AuthorizedAtUtc = ClockBase,
            AuthorizedAction = action,
            TriggerReason = "nightly"
        });

    private static PowerResult AcceptedResult() => new()
    {
        Outcome = PowerOutcome.Accepted,
        WasSimulated = false,
        Message = "real power accepted"
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

    private sealed class CountingHandler : IScheduledTaskHandler
    {
        private readonly IScheduledTaskHandler _inner;

        public CountingHandler(IScheduledTaskHandler inner) => _inner = inner;

        public int CallCount { get; private set; }

        public async Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            CallCount++;
            await _inner.HandleDueAsync(instance, cancellationToken);
        }
    }

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public FixedConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedUnattendedPolicyService : IUnattendedPolicyService
    {
        private readonly Func<PowerAction, UnattendedAuthorizationDecision> _evaluate;

        public FixedUnattendedPolicyService(Func<PowerAction, UnattendedAuthorizationDecision> evaluate)
            => _evaluate = evaluate;

        public Task<UnattendedAuthorizationDecision> EvaluateAsync(
            PowerAction action,
            CancellationToken cancellationToken) => Task.FromResult(_evaluate(action));

        public Task<UnattendedEnableResult> EnableAsync(
            UnattendedEnableRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UnattendedRevokeResult> RevokeAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPowerService : IPowerService
    {
        private readonly List<PowerRequest> _requests = new();
        private readonly PowerResult _result;

        public RecordingPowerService(PowerResult result) => _result = result;

        public IReadOnlyList<PowerRequest> Requests => _requests;

        public Task<PowerResult> ExecuteAsync(PowerRequest request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class StubIdleMonitor : IIdleMonitor
    {
        public TimeSpan? IdleDuration { get; set; }

        public bool IsMonitoring => true;

        public void Start() { }

        public void Stop() { }

        public TimeSpan? GetIdleDuration() => IdleDuration;
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
