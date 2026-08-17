using System.Diagnostics;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Remote;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Unattended;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S23 CP3 引擎远程命令：RemoteTriggerTaskCommand / RemoteCancelTaskCommand 走本地调度引擎
/// 唯一接入/仲裁路径。覆盖：等效确认立即执行、无人值守无等效 fail-closed、本地倒计时回退
/// （到期经边界执行）、并发去重、禁用/缺失拒绝、取消仅限 Confirming。全程真实引擎 + 替身
/// 电源/无人值守策略；绝不触发真实电源，绝不出现第二个电源出口。
/// </summary>
public sealed class S23_CP3_EngineRemoteCommandTests
{
    private static readonly TimeZoneInfo FixedUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "FixedUtc8",
        TimeSpan.FromHours(8),
        "FixedUtc8",
        "FixedUtc8");

    private static readonly DateTimeOffset Base = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    // ---- 等效确认：无人值守 + EquivalentUnattendedAllowed + 授权有效 → 立即执行 ----

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_Unattended_Equivalent_Executes_SinglePower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId, useUnattended: true), Base);

        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, harness.Handler.CallCount);
        Assert.Single(power.Requests);
        // 等效路径同步执行完毕；周期任务改期回 Waiting。
        Assert.Equal(TaskInstanceState.Waiting, Current(harness.Engine.GetSnapshot())!.State);
    }

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_Unattended_NoEquivalent_FailClosed_NoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId, useUnattended: true), Base);

        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: false),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SchedulerCommandStatus.InvalidCommand, result.Status);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(power.Requests);
        Assert.Null(Current(harness.Engine.GetSnapshot()));
    }

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_Unattended_Equivalent_PolicyDenied_CancelledNoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ =>
            UnattendedAuthorizationDecision.Denied(UnattendedPolicyStatus.Expired, "expired"));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId, useUnattended: true), Base);

        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SchedulerCommandStatus.TransitionRejected, result.Status);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(power.Requests);
        Assert.Equal(TaskInstanceState.Cancelled, Current(harness.Engine.GetSnapshot())!.State);
    }

    // ---- 本地倒计时回退：人工确认任务无等效 → 创建未来倒计时，到期经边界执行 ----

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_Manual_CountdownFallback_ExecutesAtFire_SinglePower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId), Base);

        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: false),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, harness.Handler.CallCount); // 尚未到期：不立即执行。
        Assert.Empty(power.Requests);
        var instance = Current(harness.Engine.GetSnapshot());
        Assert.Equal(TaskInstanceState.Confirming, instance!.State);
        Assert.Equal(Base.AddSeconds(RemoteProtocol.FallbackCountdownSeconds), instance.ScheduledFireTime);

        // 到期：推进时钟 + 完成 deadline → 倒计时边界裁决 → 执行。
        harness.Clock.UtcNow = Base.AddSeconds(RemoteProtocol.FallbackCountdownSeconds);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Handler.CallCount >= 1);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_Manual_EquivalentFlagIsIrrelevant_CountdownFallback()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId), Base);

        // 任务非无人值守：即便等效标志为 true 也无无人值守策略可等效，仍走本地倒计时。
        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(power.Requests);
        var instance = Current(harness.Engine.GetSnapshot());
        Assert.Equal(TaskInstanceState.Confirming, instance!.State);
        Assert.True(instance.ScheduledFireTime > Base);
    }

    // ---- 拒绝路径：禁用 / 缺失 / 并发去重 ----

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_DisabledTask_Rejected_NoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        var task = DailyAtTask(taskId) with { IsEnabled = false };
        using var harness = CreateHarness(power, policy, task, Base);

        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SchedulerCommandStatus.InvalidCommand, result.Status);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_UnknownTask_NoCurrentTask()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(Guid.NewGuid()), Base);

        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SchedulerCommandStatus.NoCurrentTask, result.Status);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 3000)]
    public async Task RemoteTrigger_ActiveInstance_Redundant_NoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId), Base);

        // 本地调度器已有等待实例（唯一事实源）。
        var created = await harness.Engine.SubmitAsync(
            new CreateTaskCommand(DailyAtTask(taskId)),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => Current(harness.Engine.GetSnapshot())?.State == TaskInstanceState.Waiting);

        var result = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SchedulerCommandStatus.ActiveTaskExists, result.Status);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(power.Requests);
    }

    // ---- 远程取消：仅限 Confirming ----

    [Fact(Timeout = 3000)]
    public async Task RemoteCancel_Confirming_Cancels_NoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId), Base);

        // 先起一个倒计时回退实例（Confirming，未来 fire）。
        var triggered = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: false),
            CancellationToken.None);
        Assert.True(triggered.Succeeded);
        Assert.Equal(TaskInstanceState.Confirming, Current(harness.Engine.GetSnapshot())!.State);

        var cancel = await harness.Engine.SubmitAsync(
            new RemoteCancelTaskCommand(taskId),
            CancellationToken.None);

        Assert.True(cancel.Succeeded);
        Assert.Equal(TaskInstanceState.Cancelled, Current(harness.Engine.GetSnapshot())!.State);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 3000)]
    public async Task RemoteCancel_NotConfirming_Rejected()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        // 无人值守 + 等效确认：立即执行并改期回 Waiting（非 Confirming），远程取消被拒。
        using var harness = CreateHarness(
            power,
            policy,
            DailyAtTask(taskId, useUnattended: true),
            Base);

        var triggered = await harness.Engine.SubmitAsync(
            new RemoteTriggerTaskCommand(taskId, EquivalentUnattendedAllowed: true),
            CancellationToken.None);
        Assert.True(triggered.Succeeded);
        Assert.Equal(TaskInstanceState.Waiting, Current(harness.Engine.GetSnapshot())!.State);

        var cancel = await harness.Engine.SubmitAsync(
            new RemoteCancelTaskCommand(taskId),
            CancellationToken.None);

        Assert.False(cancel.Succeeded);
        Assert.Equal(SchedulerCommandStatus.TransitionRejected, cancel.Status);
    }

    [Fact(Timeout = 3000)]
    public async Task RemoteCancel_NoInstance_NoCurrentTask()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateHarness(power, policy, DailyAtTask(taskId), Base);

        var cancel = await harness.Engine.SubmitAsync(
            new RemoteCancelTaskCommand(taskId),
            CancellationToken.None);

        Assert.False(cancel.Succeeded);
        Assert.Equal(SchedulerCommandStatus.NoCurrentTask, cancel.Status);
    }

    // ---- 夹具：真实引擎 + 真实 handler/workflow + 替身电源/无人值守策略 ----

    private static RemoteHarness CreateHarness(
        RecordingPowerService power,
        FixedUnattendedPolicyService policy,
        TaskDefinition seedTask,
        DateTimeOffset now)
    {
        var storage = new InMemoryStorage();
        var document = new TasksDocument { Tasks = [seedTask] };
        storage.Seed(TasksDocumentStore.FileName, JsonSerializer.Serialize(document));

        var clock = new FakeClock(now, FixedUtc8);
        var deadline = new ControllableDeadline();
        var evaluator = new UnattendedConfirmationEvaluator();
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: policy,
            unattendedEvaluator: evaluator);
        var handler = new CountingHandler(new ShutdownScheduledTaskHandler(workflow));
        var identifierGenerator = new CountingIdentifierGenerator();

        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            identifierGenerator);

        var engine = new SchedulerEngine(
            storage,
            clock,
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            identifierGenerator,
            handler,
            new NoOpTaskArbitrator(),
            unattendedPolicyService: policy,
            unattendedEvaluator: evaluator);

        var scope = new EngineScope(engine);
        WaitUntilRunning(engine);
        return new RemoteHarness(engine, handler, clock, deadline, scope);
    }

    private static void WaitUntilRunning(SchedulerEngine engine)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 3000)
        {
            if (engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running)
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The scheduler engine did not become ready.");
    }

    private static AppConfig RealPowerConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskDefinition DailyAtTask(
        Guid id,
        bool realPowerConfirmed = true,
        bool useUnattended = false) => new()
        {
            Id = id,
            Kind = TaskKind.DailyAt,
            Action = PowerAction.Shutdown,
            TargetTimeOfDay = new TimeOnly(9, 0, 0),
            CreatedAt = Base,
            RealPowerConfirmed = realPowerConfirmed,
            UseUnattended = useUnattended
        };

    private static UnattendedAuthorizationDecision Authorized(PowerAction action)
        => UnattendedAuthorizationDecision.Granted(new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 3,
            AuthorizedAtUtc = Base,
            AuthorizedAction = action,
            TriggerReason = "remote"
        });

    private static PowerResult AcceptedResult() => new()
    {
        Outcome = PowerOutcome.Accepted,
        WasSimulated = false,
        Message = "real power accepted"
    };

    private static TaskInstance? Current(SchedulerSnapshot snapshot)
        => snapshot.Instances.Values.FirstOrDefault();

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

    private sealed record RemoteHarness(
        SchedulerEngine Engine,
        CountingHandler Handler,
        FakeClock Clock,
        ControllableDeadline Deadline,
        EngineScope Scope) : IDisposable
    {
        public void Dispose() => Scope.Dispose();
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
        private readonly TimeZoneInfo _timeZone;

        public FakeClock(DateTimeOffset utcNow, TimeZoneInfo timeZone)
        {
            UtcNow = utcNow;
            _timeZone = timeZone;
        }

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone => _timeZone;
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
