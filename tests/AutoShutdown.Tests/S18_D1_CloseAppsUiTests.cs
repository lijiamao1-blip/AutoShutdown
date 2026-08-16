using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18-D1 返修 UI 切片测试。验证关闭应用设置页：逐目标强杀授权必须经确认回调显式 opt-in
/// （默认关闭、拒绝即回滚）、风险可见、从配置加载（绝不因缺失/非法配置自动启用强杀）、
/// 添加/移除/保存目标、逐目标强杀授权持久化。全程不触碰真实进程、真实电源或 MessageBox。
/// </summary>
public sealed class S18_D1_CloseAppsUiTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ---- 目标行：强杀授权 opt-in ----

    [Fact]
    public void Row_ForceKillOptIn_Refused_StaysDisabled()
    {
        var row = new CloseAppsTargetRow("notepad.exe", NotepadPath, null, null, false, () => false);

        row.ForceKillAllowed = true;

        Assert.False(row.ForceKillAllowed);
        Assert.False(row.RiskVisible);
    }

    [Fact]
    public void Row_ForceKillOptIn_Confirmed_EnablesAndShowsRisk()
    {
        var row = new CloseAppsTargetRow("notepad.exe", NotepadPath, null, null, false, () => true);

        row.ForceKillAllowed = true;

        Assert.True(row.ForceKillAllowed);
        Assert.True(row.RiskVisible);
    }

    [Fact]
    public void Row_ForceKillDisable_DoesNotRequireConfirmation()
    {
        var calls = 0;
        var row = new CloseAppsTargetRow("notepad.exe", NotepadPath, null, null, true, () =>
        {
            calls++;
            return false;
        });

        row.ForceKillAllowed = false;

        Assert.False(row.ForceKillAllowed);
        Assert.False(row.RiskVisible);
        Assert.Equal(0, calls);
    }

    // ---- 配置加载：绝不自动启用强杀 ----

    [Fact]
    public async Task InitializeAsync_LoadsTargets_WithForceKillDefaultOff()
    {
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ForceKillAllowed = false }),
            closeAppsForceKillConfirmation: () => true);

        await viewModel.InitializeAsync();

        var row = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Equal("notepad.exe", row.Identifier);
        Assert.Equal(NotepadPath, row.ExecutablePath);
        Assert.False(row.ForceKillAllowed);
        Assert.False(row.RiskVisible);
    }

    [Fact]
    public async Task InitializeAsync_ConfigMissingCloseApps_NoTargets_NoAutoEnable()
    {
        // 缺省/无 CloseApps 段 → 空目标，绝不凭空创建目标或启用强杀。
        var viewModel = CreateViewModel(ConfigWithoutCloseApps());

        await viewModel.InitializeAsync();

        Assert.Empty(viewModel.CloseAppsTargets);
        Assert.False(viewModel.HasCloseAppsError);
    }

    [Fact]
    public async Task RefreshConfiguration_ConfigUnavailable_ClearsTargets()
    {
        var config = new RecordingConfigurationService(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ForceKillAllowed = false }));
        var viewModel = CreateViewModel(config);
        await viewModel.InitializeAsync();
        Assert.NotEmpty(viewModel.CloseAppsTargets);

        // 配置转为不可用 → 目标清空，不残留授权（fail-closed）。
        config.LoadStatus = ConfigurationLoadStatus.Invalid;
        await viewModel.RefreshConfigurationAsync();

        Assert.Empty(viewModel.CloseAppsTargets);
    }

    // ---- 添加 / 移除目标 ----

    [Fact]
    public async Task AddTarget_ByPid_AddsRowDefaultOff()
    {
        var viewModel = CreateViewModel(ConfigWithoutCloseApps());
        await viewModel.InitializeAsync();

        viewModel.CloseAppsTargetInputText = "1234";
        viewModel.AddCloseAppsTargetCommand.Execute(null);

        var row = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Equal("pid:1234", row.Identifier);
        Assert.Equal(1234, row.ProcessId);
        Assert.Null(row.ExecutablePath);
        Assert.False(row.ForceKillAllowed);
    }

    [Fact]
    public async Task AddTarget_ByPath_AddsRowDefaultOff()
    {
        var viewModel = CreateViewModel(ConfigWithoutCloseApps());
        await viewModel.InitializeAsync();

        viewModel.CloseAppsTargetInputText = NotepadPath;
        viewModel.AddCloseAppsTargetCommand.Execute(null);

        var row = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Equal("notepad.exe", row.Identifier);
        Assert.Equal(NotepadPath, row.ExecutablePath);
        Assert.Null(row.ProcessId);
        Assert.False(row.ForceKillAllowed);
    }

    [Fact]
    public async Task AddTarget_DuplicatePath_ShowsError()
    {
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ForceKillAllowed = false }));
        await viewModel.InitializeAsync();

        viewModel.CloseAppsTargetInputText = NotepadPath;
        viewModel.AddCloseAppsTargetCommand.Execute(null);

        Assert.Single(viewModel.CloseAppsTargets);
        Assert.True(viewModel.HasCloseAppsError);
        Assert.Contains("已存在", viewModel.CloseAppsErrorText);
    }

    [Fact]
    public async Task AddTarget_EmptyInput_ShowsError()
    {
        var viewModel = CreateViewModel(ConfigWithoutCloseApps());
        await viewModel.InitializeAsync();

        viewModel.CloseAppsTargetInputText = "   ";
        viewModel.AddCloseAppsTargetCommand.Execute(null);

        Assert.Empty(viewModel.CloseAppsTargets);
        Assert.True(viewModel.HasCloseAppsError);
    }

    [Fact]
    public async Task RemoveTarget_RemovesRow()
    {
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ForceKillAllowed = false }));
        await viewModel.InitializeAsync();
        var row = Assert.Single(viewModel.CloseAppsTargets);

        viewModel.RemoveCloseAppsTargetCommand.Execute(row);

        Assert.Empty(viewModel.CloseAppsTargets);
    }

    // ---- 保存：逐目标强杀授权持久化 ----

    [Fact]
    public async Task Save_PersistsTargets_AndForceKillOptIn()
    {
        var config = new RecordingConfigurationService(ConfigWithCloseApps(
            new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ForceKillAllowed = false }));
        var viewModel = CreateViewModel(config, closeAppsForceKillConfirmation: () => true);
        await viewModel.InitializeAsync();

        // 用户对既有目标显式 opt-in 强杀。
        viewModel.CloseAppsTargets[0].ForceKillAllowed = true;

        await viewModel.SaveCloseAppsCommand.ExecuteAsync();

        var saved = Assert.Single(config.SavedConfigs);
        var target = Assert.Single(saved.CloseApps!.Targets);
        Assert.Equal(NotepadPath, target.ExecutablePath);
        Assert.True(target.ForceKillAllowed);
        Assert.Contains("已保存", viewModel.CloseAppsStatusText);
    }

    [Fact]
    public async Task Save_AddThenPersist_UsesExactOneOfPathOrPid()
    {
        var config = new RecordingConfigurationService(ConfigWithoutCloseApps());
        var viewModel = CreateViewModel(config);
        await viewModel.InitializeAsync();

        viewModel.CloseAppsTargetInputText = "4321";
        viewModel.AddCloseAppsTargetCommand.Execute(null);
        await viewModel.SaveCloseAppsCommand.ExecuteAsync();

        var saved = Assert.Single(config.SavedConfigs);
        var target = Assert.Single(saved.CloseApps!.Targets);
        Assert.Equal(4321, target.ProcessId);
        Assert.Null(target.ExecutablePath);
        Assert.False(target.ForceKillAllowed);
    }

    // ---- 夹具 ----

    private static AppConfig ConfigWithoutCloseApps() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static AppConfig ConfigWithCloseApps(params CloseAppsTargetConfig[] targets) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        CloseApps = new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 30,
            Targets = targets
        }
    };

    private static MainWindowViewModel CreateViewModel(
        AppConfig config,
        Func<bool>? closeAppsForceKillConfirmation = null)
        => CreateViewModel(
            new RecordingConfigurationService(config),
            closeAppsForceKillConfirmation);

    private static MainWindowViewModel CreateViewModel(
        RecordingConfigurationService config,
        Func<bool>? closeAppsForceKillConfirmation = null)
        => new(
            new RunningEngine(),
            config,
            new FixedClock(Now),
            new NullLogger(),
            new FakeAutoStartService(),
            autoStartConfirmation: () => true,
            cancelConfirmation: () => true,
            realPowerConfirmation: () => true,
            closeAppsForceKillConfirmation: closeAppsForceKillConfirmation);

    private sealed class RunningEngine : ISchedulerEngine
    {
        public SchedulerSnapshot GetSnapshot() => new()
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = new Dictionary<Guid, TaskInstance>(),
            LastUpdatedAt = default
        };

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
            => Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = GetSnapshot(),
                Message = "ok"
            });
    }

    private sealed class RecordingConfigurationService : IConfigurationService
    {
        private AppConfig _config;

        public RecordingConfigurationService(AppConfig config) => _config = config;

        public List<AppConfig> SavedConfigs { get; } = [];

        public ConfigurationLoadStatus LoadStatus { get; set; } = ConfigurationLoadStatus.Success;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = LoadStatus,
                Config = LoadStatus == ConfigurationLoadStatus.Success ? _config : null
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
        {
            SavedConfigs.Add(config);
            _config = config;
            return Task.FromResult(new ConfigurationSaveResult
            {
                Status = ConfigurationSaveStatus.Success
            });
        }

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class FakeAutoStartService : IAutoStartService
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
