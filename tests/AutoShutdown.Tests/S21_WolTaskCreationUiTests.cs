using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Rtc;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.WakeOnLan;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C4：唤醒他机（WoL）任务创建 UI 切片。WoL 不是电源动作：不经双闸门（不调用
/// 人工确认、RealPowerConfirmed=false、不带无人值守选项）；必须显式选择目标机器；
/// 不支持空闲触发；携带 TargetMachineId。纯内存，无真实电源、无 MessageBox。
/// </summary>
public sealed class S21_WolTaskCreationUiTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- 创建：WoL 不经双闸门 ----

    [Fact]
    public async Task WolAction_WithTargetAndCountdown_BuildsDefinition_WithoutPowerConfirmation()
    {
        var confirmCalls = 0;
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            realPowerConfirmation: () =>
            {
                confirmCalls++;
                return true;
            });
        await viewModel.RefreshConfigurationAsync();

        viewModel.SelectedAction = PowerAction.WakeOnLan;
        viewModel.SelectedMode = TimeMode.Countdown;
        SetCountdown(viewModel, "0", "1", "0");
        viewModel.SelectedWolTargetId = TargetId;

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(PowerAction.WakeOnLan, definition.Action);
        Assert.Equal(TargetId, definition.TargetMachineId);
        Assert.False(definition.RealPowerConfirmed); // 不经双闸门
        Assert.False(definition.UseUnattended);
        Assert.Null(definition.RtcWakeTimeUtc);
        Assert.Equal(0, confirmCalls); // 不调用人工确认
    }

    [Fact]
    public async Task WolAction_WithoutTarget_Rejects()
    {
        var viewModel = CreateViewModel(RealPowerConfig());
        await viewModel.RefreshConfigurationAsync();

        viewModel.SelectedAction = PowerAction.WakeOnLan;
        viewModel.SelectedMode = TimeMode.Countdown;
        SetCountdown(viewModel, "0", "1", "0");
        viewModel.SelectedWolTargetId = null;

        Assert.False(viewModel.TryBuildDefinition(out _, out var error));
        Assert.Contains("请选择要唤醒的目标机器", error);
    }

    [Fact]
    public async Task WolAction_IdleMode_Rejects()
    {
        var viewModel = CreateViewModel(RealPowerConfig());
        await viewModel.RefreshConfigurationAsync();

        viewModel.SelectedAction = PowerAction.WakeOnLan;
        viewModel.SelectedMode = TimeMode.Idle;
        viewModel.SelectedWolTargetId = TargetId;

        Assert.False(viewModel.TryBuildDefinition(out _, out var error));
        Assert.Contains("空闲触发", error);
    }

    [Fact]
    public async Task PowerAction_DoesNotCarryTargetMachineId()
    {
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            realPowerConfirmation: () => true);
        await viewModel.RefreshConfigurationAsync();

        viewModel.SelectedAction = PowerAction.Shutdown;
        viewModel.SelectedMode = TimeMode.Countdown;
        SetCountdown(viewModel, "0", "1", "0");
        viewModel.SelectedWolTargetId = TargetId;

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(PowerAction.Shutdown, definition.Action);
        Assert.Null(definition.TargetMachineId);
        Assert.True(definition.RealPowerConfirmed); // 电源动作仍走人工确认
    }

    // ---- 无人值守选项：WoL 不可见 ----

    [Fact]
    public async Task UnattendedOption_NotVisibleForWol()
    {
        var viewModel = CreateViewModel(RealPowerConfig());
        await viewModel.RefreshConfigurationAsync();

        Assert.True(viewModel.IsUnattendedTaskOptionVisible); // 默认电源动作可见

        viewModel.SelectedAction = PowerAction.WakeOnLan;
        Assert.False(viewModel.IsUnattendedTaskOptionVisible);
        Assert.False(viewModel.IsUnattendedTaskOptionAvailable);
    }

    // ---- 绑定行为 ----

    [Fact]
    public async Task ActionIsWakeOnLan_BindingBehavior()
    {
        var viewModel = CreateViewModel(RealPowerConfig());
        await viewModel.RefreshConfigurationAsync();

        viewModel.ActionIsWakeOnLan = true;
        Assert.Equal(PowerAction.WakeOnLan, viewModel.SelectedAction);
        Assert.True(viewModel.IsWolTargetSelectorVisible);

        viewModel.ActionIsShutdown = true;
        Assert.Equal(PowerAction.Shutdown, viewModel.SelectedAction);
        Assert.False(viewModel.IsWolTargetSelectorVisible);
    }

    // ---- 可创建性（引擎运行中） ----

    [Fact]
    public async Task CanCreateNow_Wol_RequiresTargetAndClockMode()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running }
        };
        var viewModel = CreateViewModel(RealPowerConfig(), engine: engine);
        await viewModel.RefreshConfigurationAsync();
        viewModel.Refresh(engine.Snapshot, Now);

        viewModel.SelectedAction = PowerAction.WakeOnLan;
        viewModel.SelectedMode = TimeMode.Countdown;
        SetCountdown(viewModel, "0", "1", "0");
        viewModel.SelectedWolTargetId = null;

        Assert.False(viewModel.CanCreateNow(out var reason));
        Assert.Contains("请选择要唤醒的目标机器", reason);

        viewModel.SelectedWolTargetId = TargetId;
        Assert.True(viewModel.CanCreateNow(out _));

        viewModel.SelectedMode = TimeMode.Idle;
        Assert.False(viewModel.CanCreateNow(out reason));
        Assert.Contains("空闲触发", reason);
    }

    [Fact]
    public async Task CanCreateNow_WolWithoutTarget_FailClosedOnEmptyGuid()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running }
        };
        var viewModel = CreateViewModel(RealPowerConfig(), engine: engine);
        await viewModel.RefreshConfigurationAsync();
        viewModel.Refresh(engine.Snapshot, Now);

        viewModel.SelectedAction = PowerAction.WakeOnLan;
        viewModel.SelectedMode = TimeMode.Countdown;
        SetCountdown(viewModel, "0", "1", "0");
        viewModel.SelectedWolTargetId = Guid.Empty;

        Assert.False(viewModel.CanCreateNow(out var reason));
        Assert.Contains("请选择要唤醒的目标机器", reason);
    }

    // ---- 启动刷新设置页分区 ----

    [Fact]
    public async Task InitializeAsync_RefreshesWolAndRtcSections()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running }
        };
        var wolSection = new WolTargetsSectionViewModel(
            new TargetMachineManager(new TargetMachineStore(
                new S21_TargetMachineStoreTests.InMemoryStorage())),
            new NoOpWakeOnLanService());
        var rtcSection = new RtcStatusSectionViewModel(
            FakeRtcWakeService.Capability(new RtcWakeCapabilityResult
            {
                Status = RtcWakeCapabilityStatus.Supported,
                WakeScope = RtcWakeScope.SuspendAndPowerOff
            }));

        var viewModel = new MainWindowViewModel(
            engine,
            new RecordingConfigurationService(RealPowerConfig()),
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService(),
            wolTargetsSection: wolSection,
            rtcStatusSection: rtcSection);

        await viewModel.InitializeAsync();

        Assert.Empty(wolSection.Targets); // NotFound → 空清单
        Assert.False(wolSection.HasError);
        Assert.Equal("支持（含完全关机后自动唤醒）", rtcSection.StatusText);
    }

    // ---- 夹具 ----

    private static void SetCountdown(MainWindowViewModel viewModel, string h, string m, string s)
    {
        viewModel.CountdownHoursText = h;
        viewModel.CountdownMinutesText = m;
        viewModel.CountdownSecondsText = s;
    }

    private static MainWindowViewModel CreateViewModel(
        AppConfig config,
        FakeSchedulerEngine? engine = null,
        Func<bool>? realPowerConfirmation = null) => new(
            engine ?? new FakeSchedulerEngine(),
            new RecordingConfigurationService(config),
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService(),
            realPowerConfirmation: realPowerConfirmation);

    private static AppConfig RealPowerConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private sealed class RecordingConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public RecordingConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
    }

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

    private sealed class NoOpWakeOnLanService : IWakeOnLanService
    {
        public Task<WolSendResult> SendAsync(Guid targetMachineId, CancellationToken cancellationToken)
            => Task.FromResult(WolSendResult.Success("sent"));
    }

    private sealed class FakeRtcWakeService : IRtcWakeService
    {
        private readonly Func<CancellationToken, Task<RtcWakeCapabilityResult>> _capability;

        private FakeRtcWakeService(Func<CancellationToken, Task<RtcWakeCapabilityResult>> capability)
            => _capability = capability;

        public static FakeRtcWakeService Capability(RtcWakeCapabilityResult result)
            => new(_ => Task.FromResult(result));

        public Task<RtcWakeCapabilityResult> GetCapabilityAsync(CancellationToken cancellationToken)
            => _capability(cancellationToken);

        public Task<RtcWakeSetResult> SetWakeAsync(DateTimeOffset wakeTimeUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RtcWakeClearResult> ClearAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
