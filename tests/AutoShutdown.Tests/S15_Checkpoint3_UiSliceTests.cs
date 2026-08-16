using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S15 检查点 3 UI 切片契约测试：TimeMode.Idle 字段映射、空闲阈值继承/显式、
/// 倒计时来源（空闲触发/定时排程）、输入恢复取消结果呈现。纯内存，无真实电源。
/// </summary>
public sealed class S15_Checkpoint3_UiSliceTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryBuildDefinition_Idle_IndexZero_InheritsGlobalDefault()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Idle;
        viewModel.IdleThresholdIndex = 0;

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.Idle, definition.Kind);
        Assert.Null(definition.IdleThresholdSeconds);
        Assert.Null(definition.CountdownDuration);
        Assert.Null(definition.TargetTimeOfDay);
    }

    [Fact]
    public void TryBuildDefinition_Idle_ExplicitThreshold_MapsSeconds()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Idle;
        viewModel.IdleThresholdIndex = 5; // 空闲 30 分钟

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.Idle, definition.Kind);
        Assert.Equal(1800, definition.IdleThresholdSeconds);
    }

    [Fact]
    public void TryBuildTimeInput_Idle_NoDurationNoTarget()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Idle;

        Assert.True(viewModel.TryBuildTimeInput(out var duration, out var target, out _));
        Assert.Null(duration);
        Assert.Null(target);
    }

    [Fact]
    public void IdleMode_VisibilityFlags()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedMode = TimeMode.Idle;
        Assert.True(viewModel.ModeIsIdle);
        Assert.True(viewModel.IsIdleSelectorVisible);
        Assert.False(viewModel.IsTimeInputVisible);
        Assert.False(viewModel.IsHolidayInputVisible);

        viewModel.SelectedMode = TimeMode.Countdown;
        Assert.False(viewModel.ModeIsIdle);
        Assert.False(viewModel.IsIdleSelectorVisible);
        Assert.False(viewModel.IsTimeInputVisible); // 倒计时也不显示目标时间

        viewModel.SelectedMode = TimeMode.DailyAt;
        Assert.True(viewModel.IsTimeInputVisible);
        Assert.True(viewModel.IsHolidayInputVisible);
    }

    [Fact]
    public void UiTextMapper_RecoveryCancelled_MapsRecoveryResult()
    {
        var recovered = IdleInstance(TaskInstanceState.Cancelled, isIdleTriggered: true, isIdleRecovered: true);
        Assert.Equal("已取消（输入恢复）", UiTextMapper.Map(recovered));

        var userCancelled = IdleInstance(TaskInstanceState.Cancelled, isIdleTriggered: true, isIdleRecovered: false);
        Assert.Equal("已取消", UiTextMapper.Map(userCancelled));
    }

    [Fact]
    public void Refresh_IdleTriggered_SetsCountdownSourceAndTriggerText()
    {
        var instance = IdleInstance(TaskInstanceState.Confirming, isIdleTriggered: true, isIdleRecovered: false);
        var viewModel = CreateViewModel(instance);

        viewModel.Refresh(RunningSnapshot(instance), Now);

        Assert.Equal("空闲触发", viewModel.CountdownSourceText);
        Assert.Equal("空闲", viewModel.TaskItems.Single().TriggerText);
    }

    [Fact]
    public void Refresh_RecoveryCancelled_SetsRecoveryResultStateText()
    {
        var instance = IdleInstance(TaskInstanceState.Cancelled, isIdleTriggered: true, isIdleRecovered: true);
        var viewModel = CreateViewModel(instance);

        viewModel.Refresh(RunningSnapshot(instance), Now);

        Assert.Equal("已取消（输入恢复）", viewModel.CurrentStateText);
        Assert.Equal("已取消（输入恢复）", viewModel.TaskItems.Single().StateText);
    }

    // ---- 夹具 ----

    private static MainWindowViewModel CreateViewModel(TaskInstance? instance = null)
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = instance is null ? RunningEmpty() : RunningSnapshot(instance)
        };

        return new MainWindowViewModel(
            engine,
            new RecordingConfigurationService(),
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService());
    }

    private static SchedulerSnapshot RunningSnapshot(TaskInstance instance) => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        Instances = new Dictionary<Guid, TaskInstance> { [instance.SourceTaskId] = instance },
        LastUpdatedAt = Now
    };

    private static SchedulerSnapshot RunningEmpty() => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        Instances = new Dictionary<Guid, TaskInstance>(),
        LastUpdatedAt = Now
    };

    private static TaskInstance IdleInstance(
        TaskInstanceState state,
        bool isIdleTriggered,
        bool isIdleRecovered) => new()
        {
            InstanceId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            SourceTaskId = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            ActionSnapshot = PowerAction.Shutdown,
            State = state,
            ScheduledFireTime = Now.AddSeconds(60),
            WarningStartTime = null,
            StageToken = Guid.Parse("20000000-0000-0000-0000-000000000003"),
            HasExecuted = false,
            IsIdleTriggered = isIdleTriggered,
            IsIdleRecovered = isIdleRecovered,
            CreatedAt = Now,
            RealPowerConfirmed = false
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
