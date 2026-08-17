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
/// S20-D1 倒计时边界确认裁决的真实 SchedulerEngine 集成测试。
/// 全程使用真实 SchedulerEngine + ShutdownScheduledTaskHandler + ShutdownWorkflow +
/// 替身电源/仲裁/空闲监视器，绝不触发真实电源；无人值守策略与评估器同时注入引擎与
/// workflow（引擎边界与双闸门之二同源）。覆盖：取消胜出、输入恢复胜出、显式无人值守
/// 仅一次进入 Pipeline/电源、以及未选择/授权失效/动作不匹配/策略异常的 fail-closed。
/// </summary>
public sealed class S20_D1_SchedulerEngineUnattendedTests
{
    private static readonly DateTimeOffset ClockBase = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid IdleSourceTaskId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    // ---- (a) Confirming 到期与取消并发：取消胜出，Pipeline=0、电源=0 ----

    [Fact(Timeout = 2000)]
    public async Task ConfirmingExpiryAndCancelConcurrent_CancelWins_NoPipelineNoPower()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        var (engine, handler) = CreateUnattendedWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), policy);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(warningSeconds: 60)),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        // 进入 Confirming（告警窗口）。
        clock.UtcNow = ClockBase.AddMinutes(59);
        deadline.CompleteNext();
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        // 等待引擎在 12:00 截止时间上重新阻塞（注册新一轮 deadline waiter），
        // 否则到期裁决与取消命令的并发时序不确定（引擎可能先处理到期而非先读取消命令）。
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        // 到期边界（12:00）与取消并发：时钟已到触发时刻，取消命令经通道唤醒引擎；
        // 命令优先于 deadline（TryProcessDueAsync 的到期裁决不得先于取消命令消费）。
        // 之后再让过期的 deadline 触发，确认其不会补触发 handler（Pipeline=0、电源=0）。
        clock.UtcNow = ClockBase.AddHours(1);
        var cancelled = await engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken),
            CancellationToken.None);
        deadline.CompleteNext();
        await Task.Delay(50);

        Assert.True(cancelled.Succeeded);
        Assert.Equal(TaskInstanceState.Cancelled, Current(engine.GetSnapshot())!.State);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- (b) Idle Confirming 到期与输入恢复并发：输入恢复胜出，Pipeline=0、电源=0 ----

    [Fact(Timeout = 2000)]
    public async Task IdleConfirmingExpiryAndInputRecoveryConcurrent_RecoveryWins_NoPipelineNoPower()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var idle = new StubIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        var (engine, handler) = CreateUnattendedWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), policy, idle);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(IdleDefinition(idleThresholdSeconds: 300, warningSeconds: 60)),
            CancellationToken.None);

        // 空闲触发 → Confirming（倒计时 60s）。
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);
        Assert.True(Current(engine.GetSnapshot())!.IsIdleTriggered);

        // 输入恢复 + 到期并发：TryProcessDueAsync 先于 TryEvaluateIdleAsync 命中到期，
        // 边界裁决必须重新检测输入并让恢复胜出（不进入 Pipeline、不调用电源）。
        idle.IdleDuration = TimeSpan.Zero;
        clock.UtcNow = ClockBase.AddSeconds(60);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Cancelled);

        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Cancelled, instance.State);
        Assert.True(instance.IsIdleRecovered);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- (c) 有效授权 + 显式选择无人值守：仅一次进入 Pipeline/电源 ----

    [Fact(Timeout = 2000)]
    public async Task ValidAuthorizationAndExplicitUnattended_EntersPipelineAndPowerExactlyOnce()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        var (engine, handler) = CreateUnattendedWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), policy);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(useUnattended: true)),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;
        Assert.True(instance.UseUnattended);
        Assert.False(instance.RealPowerConfirmed);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = ClockBase.AddHours(1);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Executed);

        Assert.Equal(1, handler.CallCount);
        Assert.Single(power.Requests);
        Assert.True(power.Requests[0].RealPowerConfirmed);
        Assert.Equal(TaskInstanceState.Executed, Current(engine.GetSnapshot())!.State);
    }

    // ---- (d) 未选择无人值守 / 授权失效 / 动作不匹配 / 策略异常：不绕过人工确认，电源=0 ----

    [Fact(Timeout = 2000)]
    public async Task UnattendedNotSelected_DoesNotBypassManualConfirmation_NoPower()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new RecordingPowerService(AcceptedResult());
        // 全局授权有效，但任务未选择无人值守（且无人工确认）→ 不得自动套用全局授权。
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        var (engine, _) = CreateUnattendedWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), policy);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(useUnattended: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = ClockBase.AddHours(1);
        deadline.CompleteNext();

        // workflow 在双闸门之二拒绝（RealPowerConfirmationMissing），不进入 Pre-Pipeline、不调用电源。
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Faulted);

        Assert.Equal(TaskInstanceState.Faulted, Current(engine.GetSnapshot())!.State);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task UnattendedAuthorizationExpired_FailClosedCancels_NoPower()
    {
        var policy = new FixedUnattendedPolicyService(_ =>
            UnattendedAuthorizationDecision.Denied(UnattendedPolicyStatus.Expired, "expired"));

        await AssertBoundaryCancelsWithoutPowerAsync(policy, PowerAction.Shutdown);
    }

    [Fact(Timeout = 2000)]
    public async Task UnattendedActionMismatch_FailClosedCancels_NoPower()
    {
        var policy = new FixedUnattendedPolicyService(action =>
            action == PowerAction.Shutdown
                ? Authorized(PowerAction.Shutdown)
                : UnattendedAuthorizationDecision.Denied(UnattendedPolicyStatus.ActionMismatch, "mismatch"));

        await AssertBoundaryCancelsWithoutPowerAsync(policy, PowerAction.Hibernate);
    }

    [Fact(Timeout = 2000)]
    public async Task UnattendedPolicyThrows_FailClosedCancels_NoPower()
    {
        var policy = new FixedUnattendedPolicyService(_ =>
            throw new InvalidOperationException("storage failure"));

        await AssertBoundaryCancelsWithoutPowerAsync(policy, PowerAction.Shutdown);
    }

    private static async Task AssertBoundaryCancelsWithoutPowerAsync(
        IUnattendedPolicyService policy,
        PowerAction action)
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new RecordingPowerService(AcceptedResult());
        var (engine, handler) = CreateUnattendedWorkflowEngine(
            storage, clock, deadline, power, RealPowerConfig(), policy);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(useUnattended: true, action: action)),
            CancellationToken.None);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = ClockBase.AddHours(1);
        deadline.CompleteNext();

        // 边界裁决 fail-closed：取消，不进 Pipeline、不调用电源。
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Cancelled);

        Assert.Equal(TaskInstanceState.Cancelled, Current(engine.GetSnapshot())!.State);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- 夹具 ----

    private static (SchedulerEngine Engine, CountingHandler Handler) CreateUnattendedWorkflowEngine(
        IStorage storage,
        FakeClock clock,
        ControllableDeadline deadline,
        IPowerService power,
        AppConfig config,
        IUnattendedPolicyService? unattendedPolicyService,
        StubIdleMonitor? idleMonitor = null)
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

    private static AppConfig RealPowerConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskDefinition CountdownDefinition(
        int? warningSeconds = null,
        bool useUnattended = false,
        PowerAction action = PowerAction.Shutdown) => new()
        {
            Id = SourceTaskId,
            Kind = TaskKind.Countdown,
            Action = action,
            CountdownDuration = TimeSpan.FromHours(1),
            WarningSeconds = warningSeconds,
            CreatedAt = ClockBase,
            UseUnattended = useUnattended
        };

    private static TaskDefinition IdleDefinition(int? idleThresholdSeconds, int? warningSeconds = null) => new()
    {
        Id = IdleSourceTaskId,
        Kind = TaskKind.Idle,
        Action = PowerAction.Shutdown,
        IdleThresholdSeconds = idleThresholdSeconds,
        WarningSeconds = warningSeconds,
        CreatedAt = ClockBase
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
