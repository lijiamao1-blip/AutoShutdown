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
/// S22 CP4 测试：外部触发只回调本地调度/Workflow（绝不直接执行电源命令）。
/// 时间闸门以本地调度器为事实源裁决外部回调；触发服务 fail-closed（缺配置/损坏/禁用/
/// 偏离窗口一律不执行电源），成功时把 Executing 实例交给唯一 handler。
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

    // ---- 外部触发服务（ExternalTaskTriggerService） ----

    [Fact]
    public async Task Trigger_EnabledOnSchedule_RoutesExecutingInstanceToHandler()
    {
        var taskId = Guid.NewGuid();
        var handler = new FakeScheduledTaskHandler();
        var service = CreateTriggerService(handler, tasks: [DailyAtTask(taskId)]);

        var outcome = await service.HandleExternalTriggerAsync(taskId, CancellationToken.None);

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

    [Fact]
    public async Task Trigger_HandlerThrows_ExecutionFailedNoPower()
    {
        var taskId = Guid.NewGuid();
        var handler = new FakeScheduledTaskHandler
        {
            ThrowOnHandle = new InvalidOperationException("workflow rejected")
        };
        var service = CreateTriggerService(handler, tasks: [DailyAtTask(taskId)]);

        var outcome = await service.HandleExternalTriggerAsync(taskId, CancellationToken.None);

        Assert.Equal(ExternalTriggerOutcomeStatus.ExecutionFailed, outcome.Status);
        Assert.Equal(1, handler.CallCount); // handler 被调用但拒绝执行。
    }

    // ---- 辅助 ----

    private static ExternalTriggerScheduleGate CreateGate()
        => new(new NextExecutionCalculator());

    private static ExternalTaskTriggerService CreateTriggerService(
        FakeScheduledTaskHandler handler,
        IReadOnlyList<TaskDefinition>? tasks = null,
        InMemoryStorage? storage = null,
        DateTimeOffset? nowUtc = null)
    {
        storage ??= new InMemoryStorage();
        if (tasks is { Count: > 0 })
        {
            var document = new TasksDocument { Tasks = tasks };
            storage.Seed(TasksDocumentStore.FileName, JsonSerializer.Serialize(document));
        }

        var clock = new FixedClock(nowUtc ?? DailyFireUtc, FixedUtc8);
        var gate = CreateGate();
        var documentStore = new TasksDocumentStore(storage);
        return new ExternalTaskTriggerService(
            documentStore,
            handler,
            clock,
            gate,
            new GuidIdentifierGenerator());
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
