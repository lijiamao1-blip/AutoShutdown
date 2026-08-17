using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 CP4 + D2 外部触发测试。时间闸门以本地调度器为事实源裁决外部回调；触发服务
/// fail-closed（缺配置/损坏/禁用/偏离窗口一律不执行电源）；成功路径把触发经
/// ExternalTriggerTaskCommand 提交进本地调度引擎唯一接入/仲裁路径执行，绝不直接调用
/// handler 或 Workflow（D2）。引擎内并发去重与 S20-D1/D2 边界裁决由
/// S22_D2_ExternalTriggerIntegrationTests 覆盖。
/// </summary>
public sealed class S22_ExternalTriggerTests
{
    private static readonly TimeZoneInfo FixedUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "FixedUtc8",
        TimeSpan.FromHours(8),
        "FixedUtc8",
        "FixedUtc8");

    // 09:00 本地（Utc+8）= 01:00 UTC。
    private static readonly DateTimeOffset DailyFireUtc = new(2026, 8, 17, 1, 0, 0, TimeSpan.Zero);

    // ---- 时间闸门（ExternalTriggerScheduleGate） ----

    [Fact]
    public void Gate_DailyAtExactlyOnFire_Allowed()
    {
        var gate = CreateGate();
        var verdict = gate.Evaluate(DailyAtTask(), DailyFireUtc, FixedUtc8);

        Assert.Equal(ExternalTriggerGateVerdict.Allowed, verdict.Verdict);
        Assert.Equal(DailyFireUtc, verdict.ExpectedFireTimeUtc);
    }

    [Fact]
    public void Gate_DailyAtWithinTolerance_Allowed()
    {
        var gate = CreateGate();
        var verdict = gate.Evaluate(
            DailyAtTask(),
            DailyFireUtc.AddMinutes(4),
            FixedUtc8);

        Assert.Equal(ExternalTriggerGateVerdict.Allowed, verdict.Verdict);
    }

    [Fact]
    public void Gate_DailyAtBeyondTolerance_OutsideWindow()
    {
        var gate = CreateGate();
        var verdict = gate.Evaluate(
            DailyAtTask(),
            DailyFireUtc.AddMinutes(6),
            FixedUtc8);

        Assert.Equal(ExternalTriggerGateVerdict.OutsideWindow, verdict.Verdict);
    }

    [Fact]
    public void Gate_DailyAtCustomTolerance_Allowed()
    {
        var gate = CreateGate();
        // +8 分钟超出默认 5 分钟容差，但在自定义 10 分钟容差内。
        var verdict = gate.Evaluate(
            DailyAtTask(),
            DailyFireUtc.AddMinutes(8),
            FixedUtc8,
            tolerance: TimeSpan.FromMinutes(10));

        Assert.Equal(ExternalTriggerGateVerdict.Allowed, verdict.Verdict);
    }

    [Fact]
    public void Gate_HourBeforeDailyFire_OutsideWindow()
    {
        var gate = CreateGate();
        var verdict = gate.Evaluate(
            DailyAtTask(),
            DailyFireUtc.AddHours(-1),
            FixedUtc8);

        Assert.Equal(ExternalTriggerGateVerdict.OutsideWindow, verdict.Verdict);
    }

    [Fact]
    public void Gate_ExpiredOneTime_NoSchedule()
    {
        var gate = CreateGate();
        var task = new TaskDefinition
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.OneTime,
            Action = PowerAction.Shutdown,
            OneTimeDateTime = new DateTime(2026, 8, 10, 1, 0, 0, DateTimeKind.Utc), // 一周前
            CreatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            RealPowerConfirmed = true
        };

        var verdict = gate.Evaluate(task, DailyFireUtc, FixedUtc8);

        Assert.Equal(ExternalTriggerGateVerdict.NoSchedule, verdict.Verdict);
    }

    [Fact]
    public void Gate_IdleTrigger_AlwaysAllowed()
    {
        var gate = CreateGate();
        var task = new TaskDefinition
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.Idle,
            Action = PowerAction.Shutdown,
            IdleThresholdSeconds = 300,
            CreatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            RealPowerConfirmed = true
        };

        var verdict = gate.Evaluate(task, DailyFireUtc, FixedUtc8);

        Assert.Equal(ExternalTriggerGateVerdict.Allowed, verdict.Verdict);
    }

    // ---- 外部触发服务（ExternalTaskTriggerService）：fail-closed 前置校验（不经引擎） ----

    [Fact]
    public async Task Trigger_UnknownLocalTask_TaskNotFoundAndNoPower()
    {
        var handler = new FakeScheduledTaskHandler();
        var service = CreateTriggerService(handler, tasks: [DailyAtTask()]);

        var outcome = await service.HandleExternalTriggerAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.TaskNotFound, outcome.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Trigger_EmptyTaskId_TaskNotFoundAndNoPower()
    {
        var handler = new FakeScheduledTaskHandler();
        var service = CreateTriggerService(handler, tasks: [DailyAtTask()]);

        var outcome = await service.HandleExternalTriggerAsync(Guid.Empty, CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.TaskNotFound, outcome.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Trigger_LocalTaskDisabled_DisabledAndNoPower()
    {
        var taskId = Guid.NewGuid();
        var handler = new FakeScheduledTaskHandler();
        var task = DailyAtTask(taskId) with { IsEnabled = false };
        var service = CreateTriggerService(handler, tasks: [task]);

        var outcome = await service.HandleExternalTriggerAsync(taskId, CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.Disabled, outcome.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Trigger_OutsideLocalScheduleWindow_GateRejectedAndNoPower()
    {
        var taskId = Guid.NewGuid();
        var handler = new FakeScheduledTaskHandler();
        // 固定时钟偏离 09:00 本地触发点 1 小时：近似/伪造外部触发被本地调度器闸门拒绝。
        var service = CreateTriggerService(
            handler,
            tasks: [DailyAtTask(taskId)],
            nowUtc: DailyFireUtc.AddHours(-1));

        var outcome = await service.HandleExternalTriggerAsync(taskId, CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.GateRejected, outcome.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Trigger_TasksFileMissing_FailClosedNoPower()
    {
        var handler = new FakeScheduledTaskHandler();
        // 不播种 tasks.json → NotFound → fail-closed。
        var service = CreateTriggerService(handler, tasks: Array.Empty<TaskDefinition>());

        var outcome = await service.HandleExternalTriggerAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.ConfigLoadFailed, outcome.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Trigger_TasksFileCorrupt_FailClosedNoPower()
    {
        var handler = new FakeScheduledTaskHandler();
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var service = CreateTriggerService(handler, storage: storage);

        var outcome = await service.HandleExternalTriggerAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.ConfigLoadFailed, outcome.Status);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- 外部触发服务：唯一接入路径（经运行中的本地调度引擎） ----

    [Fact]
    public async Task Trigger_EnabledOnSchedule_RoutesThroughEngineToHandler()
    {
        var taskId = Guid.NewGuid();
        var handler = new FakeScheduledTaskHandler();
        // 触发时刻 = 到期前 3 分钟：落在闸门容差内（本地允许），但本地引擎尚未接管该窗口
        // （到期优先循环不会误判冗余），外部触发经唯一接入路径执行。
        using var harness = CreateRunningHarness(
            handler,
            tasks: [DailyAtTask(taskId)],
            nowUtc: DailyFireUtc.AddMinutes(-3));

        var outcome = await harness.Service.HandleExternalTriggerAsync(taskId, CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.Success, outcome.Status);
        Assert.Equal(1, handler.CallCount);
        var instance = handler.LastInstance;
        Assert.NotNull(instance);
        Assert.Equal(TaskInstanceState.Executing, instance!.State);
        Assert.True(instance.HasExecuted);
        Assert.Equal(taskId, instance.SourceTaskId);
        Assert.Equal(PowerAction.Shutdown, instance.ActionSnapshot);
        Assert.True(instance.RealPowerConfirmed);
    }

    [Fact]
    public async Task Trigger_EngineFaultsDuringExecution_ExecutionFailedNoPower()
    {
        var taskId = Guid.NewGuid();
        var handler = new FakeScheduledTaskHandler
        {
            ThrowOnHandle = new InvalidOperationException("workflow rejected")
        };
        using var harness = CreateRunningHarness(
            handler,
            tasks: [DailyAtTask(taskId)],
            nowUtc: DailyFireUtc.AddMinutes(-3));

        var outcome = await harness.Service.HandleExternalTriggerAsync(taskId, CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.ExecutionFailed, outcome.Status);
        Assert.Equal(1, handler.CallCount); // 引擎唯一路径已调用 handler，但执行被拒。
    }

    // ---- 辅助 ----

    private static ExternalTriggerScheduleGate CreateGate()
        => new(new NextExecutionCalculator());

    /// <summary>前置校验用服务：构造引擎但不启动（fail-closed 校验不经引擎命令路径）。</summary>
    private static ExternalTaskTriggerService CreateTriggerService(
        FakeScheduledTaskHandler handler,
        IReadOnlyList<TaskDefinition>? tasks = null,
        InMemoryStorage? storage = null,
        DateTimeOffset? nowUtc = null)
    {
        var harness = BuildHarness(handler, tasks, storage, nowUtc, startEngine: false);
        return harness.Service;
    }

    /// <summary>唯一接入路径用：启动引擎并等待 Running，再返回服务。</summary>
    private static TriggerHarness CreateRunningHarness(
        FakeScheduledTaskHandler handler,
        IReadOnlyList<TaskDefinition>? tasks = null,
        InMemoryStorage? storage = null,
        DateTimeOffset? nowUtc = null)
    {
        var harness = BuildHarness(handler, tasks, storage, nowUtc, startEngine: true);
        WaitUntilRunning(harness.Engine);
        return harness;
    }

    private static TriggerHarness BuildHarness(
        FakeScheduledTaskHandler handler,
        IReadOnlyList<TaskDefinition>? tasks,
        InMemoryStorage? storage,
        DateTimeOffset? nowUtc,
        bool startEngine)
    {
        storage ??= new InMemoryStorage();
        if (tasks is { Count: > 0 })
        {
            var document = new TasksDocument { Tasks = tasks };
            storage.Seed(TasksDocumentStore.FileName, JsonSerializer.Serialize(document));
        }

        var clock = new FixedClock(nowUtc ?? DailyFireUtc, FixedUtc8);
        var engine = new SchedulerEngine(
            storage,
            clock,
            new SystemAsyncDeadline(clock),
            new TaskService(
                new NextExecutionCalculator(),
                new TaskInstanceStateMachine(),
                new GuidIdentifierGenerator()),
            new TaskInstanceStateMachine(),
            new GuidIdentifierGenerator(),
            handler,
            new NoOpTaskArbitrator());
        var documentStore = new TasksDocumentStore(storage);
        var service = new ExternalTaskTriggerService(
            documentStore,
            engine,
            clock,
            CreateGate());

        var scope = startEngine ? new EngineScope(engine) : null;
        return new TriggerHarness(service, engine, scope);
    }

    private static void WaitUntilRunning(SchedulerEngine engine)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 2000)
        {
            if (engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running)
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The scheduler engine did not become ready.");
    }

    private static TaskDefinition DailyAtTask(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = TaskKind.DailyAt,
        Action = PowerAction.Shutdown,
        TargetTimeOfDay = new TimeOnly(9, 0, 0),
        CreatedAt = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
        RealPowerConfirmed = true
    };

    private sealed record TriggerHarness(
        ExternalTaskTriggerService Service,
        SchedulerEngine Engine,
        EngineScope? Scope) : IDisposable
    {
        public void Dispose() => Scope?.Dispose();
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

    private sealed class FakeScheduledTaskHandler : IScheduledTaskHandler
    {
        public TaskInstance? LastInstance { get; private set; }

        public int CallCount { get; private set; }

        public Exception? ThrowOnHandle { get; init; }

        public Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            CallCount++;
            LastInstance = instance;
            if (ThrowOnHandle is not null)
            {
                throw ThrowOnHandle;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        private readonly DateTimeOffset _utcNow;
        private readonly TimeZoneInfo _timeZone;

        public FixedClock(DateTimeOffset utcNow, TimeZoneInfo timeZone)
        {
            _utcNow = utcNow;
            _timeZone = timeZone;
        }

        public DateTimeOffset UtcNow => _utcNow;
        public TimeZoneInfo LocalTimeZone => _timeZone;
    }
}
