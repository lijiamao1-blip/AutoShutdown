using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-UI1-D2 聚焦测试：空闲触发任务在首页「当前任务」卡显示真实空闲时长/阈值，
/// 替换误导性占位倒计时；检测失败与依赖缺失 fail-closed；非 Idle / Idle 已触发维持现状。
/// 纯内存 + 假依赖，绝不触发真实电源。
/// </summary>
public sealed class S_UI1_D2_IdleStatusDisplayTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid TaskId = Guid.Parse("d2000000-0000-0000-0000-000000000001");

    // ===== 4.1 #1：Idle 任务 Waiting → 显示真实空闲时长 + 来源「空闲触发」 =====

    [Fact]
    public void IdleWaiting_ShowsRealIdleDurationAndSource()
    {
        var definition = IdleDefinition(thresholdSeconds: null); // 继承全局默认 30 分钟
        var instance = IdleWaitingInstance();
        var idleMonitor = new FakeIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var viewModel = CreateViewModel(definition, instance, idleMonitor, taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("空闲触发", viewModel.CountdownSourceText);
        Assert.Equal("已连续空闲 5 分 0 秒 / 阈值 30 分钟", viewModel.CountdownText);
        Assert.Equal("等待连续空闲达阈值后触发", viewModel.NextFireTimeText);
        Assert.True(viewModel.IsIdleStatusVisible); // 小字号展示态，避免 40px 大字撑爆卡片
    }

    // ===== 4.1 #2：输入恢复回落 → 文本跟随真实空闲时长变小 =====

    [Fact]
    public void IdleWaiting_InputRecovery_MakesCountdownTextFollowIdleDuration()
    {
        var definition = IdleDefinition(thresholdSeconds: null);
        var instance = IdleWaitingInstance();
        var idleMonitor = new FakeIdleMonitor { IdleDuration = TimeSpan.FromMinutes(5) };
        var viewModel = CreateViewModel(definition, instance, idleMonitor, taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);
        Assert.Equal("已连续空闲 5 分 0 秒 / 阈值 30 分钟", viewModel.CountdownText);

        idleMonitor.IdleDuration = TimeSpan.FromSeconds(10); // 输入恢复，真实空闲回落
        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("已连续空闲 0 分 10 秒 / 阈值 30 分钟", viewModel.CountdownText);
    }

    // ===== 4.1 #3：检测失败 fail-closed → 不显示假数字 =====

    [Fact]
    public void IdleWaiting_DetectionFailure_FailsClosedNoFakeNumber()
    {
        var definition = IdleDefinition(thresholdSeconds: null);
        var instance = IdleWaitingInstance();
        var idleMonitor = new FakeIdleMonitor { IdleDuration = null };
        var viewModel = CreateViewModel(definition, instance, idleMonitor, taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("输入状态未知（无法确认空闲）", viewModel.CountdownText);
        Assert.DoesNotMatch("[0-9]", viewModel.CountdownText);
    }

    // ===== 4.1 #4：非 Idle 任务完全不受影响 =====

    [Fact]
    public void NonIdleTask_Unchanged()
    {
        var definition = CountdownDefinition();
        var instance = WaitingInstance(fire: Now.AddSeconds(60));
        var viewModel = CreateViewModel(definition, instance, new FakeIdleMonitor(), taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("00:01:00", viewModel.CountdownText);
        Assert.Equal("定时排程", viewModel.CountdownSourceText);
        Assert.Equal("2024-01-15 11:01:00", viewModel.NextFireTimeText);
        Assert.False(viewModel.IsIdleStatusVisible);
    }

    // ===== 4.1 #5：Idle 已触发（Confirming + IsIdleTriggered=true）维持现状 =====

    [Fact]
    public void IdleTriggeredConfirming_Unchanged()
    {
        var definition = IdleDefinition(thresholdSeconds: null);
        var instance = ConfirmingIdleInstance(fire: Now.AddSeconds(30));
        var viewModel = CreateViewModel(definition, instance, new FakeIdleMonitor { IdleDuration = TimeSpan.FromMinutes(30) }, taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("空闲触发", viewModel.CountdownSourceText);
        Assert.Equal("00:00:30", viewModel.CountdownText);
        Assert.Equal("2024-01-15 11:00:30", viewModel.NextFireTimeText);
        Assert.False(viewModel.IsIdleStatusVisible); // 已触发走真实告警窗口大号倒计时
    }

    // ===== 4.1 #6：依赖缺失安全（fail-closed，不崩溃、不显示占位数字） =====

    [Fact]
    public void IdleWaiting_MissingIdleMonitor_FailsClosedNoFakeNumber()
    {
        var definition = IdleDefinition(thresholdSeconds: null);
        var instance = IdleWaitingInstance();
        var viewModel = CreateViewModel(definition, instance, idleMonitor: null, taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("输入状态未知（无法确认空闲）", viewModel.CountdownText);
    }

    [Fact]
    public void MissingTaskService_DoesNotCrash_PreservesNonIdleCountdown()
    {
        var instance = WaitingInstance(fire: Now.AddSeconds(60));
        var viewModel = CreateViewModel(definition: null, instance, new FakeIdleMonitor(), taskService: null);

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("00:01:00", viewModel.CountdownText);
        Assert.Equal("定时排程", viewModel.CountdownSourceText);
    }

    // ===== 4.1 #7：阈值解析（显式 600 秒 / 未设置继承全局默认） =====

    [Fact]
    public void IdleThreshold_ExplicitSixHundred_ShowsTenMinutes()
    {
        var definition = IdleDefinition(thresholdSeconds: 600);
        var instance = IdleWaitingInstance();
        var viewModel = CreateViewModel(definition, instance, new FakeIdleMonitor { IdleDuration = TimeSpan.FromMinutes(3) }, taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("已连续空闲 3 分 0 秒 / 阈值 10 分钟", viewModel.CountdownText);
    }

    [Fact]
    public void IdleThreshold_InheritGlobalDefault_ShowsThirtyMinutes()
    {
        var definition = IdleDefinition(thresholdSeconds: null);
        var instance = IdleWaitingInstance();
        var viewModel = CreateViewModel(definition, instance, new FakeIdleMonitor { IdleDuration = TimeSpan.FromMinutes(3) }, taskService: new FakeTaskService(definition));

        viewModel.Refresh(SnapshotOf(instance), Now);

        Assert.Equal("已连续空闲 3 分 0 秒 / 阈值 30 分钟", viewModel.CountdownText);
    }

    // ===== 夹具 =====

    private static MainWindowViewModel CreateViewModel(
        TaskDefinition? definition,
        TaskInstance instance,
        FakeIdleMonitor? idleMonitor,
        ITaskService? taskService)
    {
        var engine = new FakeSchedulerEngine { Snapshot = SnapshotOf(instance) };
        return new MainWindowViewModel(
            engine,
            new RecordingConfigurationService(),
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService(),
            taskService: taskService,
            idleMonitor: idleMonitor);
    }

    private static TaskInstance IdleWaitingInstance() => new()
    {
        InstanceId = Guid.Parse("d2000000-0000-0000-0000-000000000002"),
        SourceTaskId = TaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Waiting,
        ScheduledFireTime = Now.AddMinutes(30), // 占位 fire time，不影响 D2 显示
        StageToken = Guid.Parse("d2000000-0000-0000-0000-000000000003"),
        HasExecuted = false,
        IsIdleTriggered = false,
        IsIdleRecovered = false,
        CreatedAt = Now,
        RealPowerConfirmed = false
    };

    private static TaskInstance ConfirmingIdleInstance(DateTimeOffset fire) => new()
    {
        InstanceId = Guid.Parse("d2000000-0000-0000-0000-000000000002"),
        SourceTaskId = TaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Confirming,
        ScheduledFireTime = fire,
        WarningStartTime = fire.AddSeconds(-30),
        StageToken = Guid.Parse("d2000000-0000-0000-0000-000000000003"),
        HasExecuted = false,
        IsIdleTriggered = true,
        IsIdleRecovered = false,
        CreatedAt = Now,
        RealPowerConfirmed = false
    };

    private static TaskInstance WaitingInstance(DateTimeOffset fire) => new()
    {
        InstanceId = Guid.Parse("d2000000-0000-0000-0000-000000000002"),
        SourceTaskId = TaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Waiting,
        ScheduledFireTime = fire,
        StageToken = Guid.Parse("d2000000-0000-0000-0000-000000000003"),
        HasExecuted = false,
        IsIdleTriggered = false,
        IsIdleRecovered = false,
        CreatedAt = Now,
        RealPowerConfirmed = false
    };

    private static TaskDefinition IdleDefinition(int? thresholdSeconds) => new()
    {
        Id = TaskId,
        Kind = TaskKind.Idle,
        Action = PowerAction.Shutdown,
        IdleThresholdSeconds = thresholdSeconds,
        CreatedAt = Now
    };

    private static TaskDefinition CountdownDefinition() => new()
    {
        Id = TaskId,
        Kind = TaskKind.Countdown,
        Action = PowerAction.Shutdown,
        CountdownDuration = TimeSpan.FromMinutes(30),
        CreatedAt = Now
    };

    private static SchedulerSnapshot SnapshotOf(TaskInstance instance) => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        Instances = new Dictionary<Guid, TaskInstance> { [instance.SourceTaskId] = instance },
        LastUpdatedAt = Now
    };

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public SchedulerSnapshot GetSnapshot() => Snapshot;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
            => Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
    }

    private sealed class FakeTaskService : ITaskService
    {
        private readonly IReadOnlyDictionary<Guid, TaskDefinition> _definitions;

        public FakeTaskService(params TaskDefinition[] definitions)
            => _definitions = definitions.ToDictionary(d => d.Id);

#pragma warning disable CS0067 // 假替身不触发集合变更事件；仅需满足接口签名
        public event EventHandler<TaskCollectionChangedEventArgs>? CollectionChanged;
#pragma warning restore CS0067

        public TaskDefinition? Get(Guid taskId)
            => _definitions.TryGetValue(taskId, out var definition) ? definition : null;

        public IReadOnlyCollection<TaskDefinition> GetAll() => _definitions.Values.ToList();

        public TaskCommandResult Create(TaskDefinition definition, DateTimeOffset now, TimeZoneInfo timeZone)
            => throw new NotSupportedException();

        public TaskCommandResult Snooze(TaskInstance current, TimeSpan duration, DateTimeOffset now)
            => throw new NotSupportedException();

        public TaskCommandResult Cancel(TaskInstance current)
            => throw new NotSupportedException();

        public TaskCommandResult RescheduleAfterArbitration(TaskInstance current, TimeSpan delay, DateTimeOffset now)
            => throw new NotSupportedException();

        public TaskCommandResult RescheduleDaily(TaskDefinition definition, TaskInstance current, DateTimeOffset now, TimeZoneInfo timeZone)
            => throw new NotSupportedException();

        public TaskCommandResult RescheduleRecurring(TaskDefinition definition, TaskInstance current, DateTimeOffset now, TimeZoneInfo timeZone)
            => throw new NotSupportedException();

        public TaskCollectionResult Add(TaskDefinition definition)
            => throw new NotSupportedException();

        public TaskCollectionResult Update(TaskDefinition definition)
            => throw new NotSupportedException();

        public TaskCollectionResult Remove(Guid taskId)
            => throw new NotSupportedException();

        public TaskCollectionResult SetEnabled(Guid taskId, bool isEnabled)
            => throw new NotSupportedException();
    }

    private sealed class FakeIdleMonitor : IIdleMonitor
    {
        public bool IsMonitoring { get; set; }

        public TimeSpan? IdleDuration { get; set; }

        public void Start() => IsMonitoring = true;

        public void Stop() => IsMonitoring = false;

        public TimeSpan? GetIdleDuration() => IdleDuration;
    }

    private sealed class RecordingConfigurationService : IConfigurationService
    {
        public ConfigurationLoadResult LoadResult { get; init; } = new()
        {
            Status = ConfigurationLoadStatus.Success,
            Config = new AppConfig
            {
                SchemaVersion = 1,
                TestMode = true,
                AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
            }
        };

        public TasksLoadResult LoadTasksResult { get; init; } = new() { Status = TasksLoadStatus.NotFound };

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(LoadResult);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });

        public Task<TasksLoadResult> LoadTasksAsync(CancellationToken cancellationToken)
            => Task.FromResult(LoadTasksResult);

        public Task<TasksSaveResult> SaveTasksAsync(TasksDocument document, CancellationToken cancellationToken)
            => Task.FromResult(new TasksSaveResult { Status = TasksSaveStatus.Success });
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class NoOpAutoStartService : IAutoStartService
    {
        public AutoStartStatus GetStatus() => AutoStartStatus.Disabled;

        public AutoStartOperationResult Enable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class NullLogger : IApplicationLogger
    {
        public string LogDirectory => "C:\\logs";

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }
}
