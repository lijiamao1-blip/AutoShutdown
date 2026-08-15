using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Notifications;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class S11UiSourceContractTests
{
    // ---- 1. 无乱码 ----

    [Fact]
    public void AppSources_HaveNoMojibake()
    {
        foreach (var file in EnumerateAppFiles("*.cs").Concat(EnumerateAppFiles("*.xaml")))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain('\uFFFD', content);
            Assert.DoesNotContain("鑷", content);
            Assert.DoesNotContain("鍏", content);
            Assert.DoesNotContain("鎵", content);
            Assert.DoesNotContain("閫€", content);
        }
    }

    // ---- 2. 全 src 无真实电源 API ----

    [Fact]
    public void WholeSource_PowerDllImportsOnlyInWin32PowerNativeApi()
    {
        foreach (var file in EnumerateWholeSourceFiles())
        {
            var name = Path.GetFileName(file);
            var content = File.ReadAllText(file);

            if (name == "Win32PowerNativeApi.cs")
            {
                Assert.Contains("DllImport", content);
            }
            else
            {
                Assert.DoesNotContain("DllImport", content);
                Assert.DoesNotContain("ExitWindowsEx", content);
                Assert.DoesNotContain("SetSuspendState", content);
            }

            Assert.DoesNotContain("shutdown.exe", content);
            Assert.DoesNotContain("Process.Start", content);
        }
    }

    // ---- 3. IPowerService 通过 GuardedPowerService 注册（默认仍走 Fake） ----

    [Fact]
    public void IPowerService_RegisteredAsGuardedPowerService()
    {
        var source = ReadAppFile("AppHost", "ServiceRegistration.cs");

        Assert.Contains("AddSingleton<FakePowerService>", source);
        Assert.Contains("new GuardedPowerService(", source);
        Assert.DoesNotContain("AddSingleton<IPowerService, FakePowerService>", source);
    }

    // ---- 4. 托盘菜单只含打开与退出 ----

    [Fact]
    public void TrayMenu_OnlyOpenAndExit()
    {
        var source = ReadAppFile("Infrastructure", "TrayIconService.cs");

        Assert.Contains("打开控制面板", source);
        Assert.Contains("退出程序", source);
        Assert.DoesNotContain("立即关机", source);
        Assert.DoesNotContain("Restart", source);
        Assert.DoesNotContain("Sleep", source);
        Assert.DoesNotContain("Hibernate", source);
    }

    // ---- 5. 导航 7 项 + 占位说明 ----

    [Fact]
    public void Navigation_HasSevenEntries_AndPlaceholderText()
    {
        var viewModel = ReadAppFile("Presentation", "MainWindowViewModel.cs");
        foreach (var title in new[] { "首页", "任务管理", "高级功能", "网络唤醒", "日志与诊断", "软件设置", "关于软件" })
        {
            Assert.Contains(title, viewModel);
        }

        var window = ReadAppFile("MainWindow.xaml");
        Assert.Contains("该功能将在后台能力通过测试后开放", window);
    }

    // ---- 6 / 15. 无立即执行 ----

    [Fact]
    public void NoImmediateExecuteButtons()
    {
        foreach (var file in new[] { "MainWindow.xaml", "ReminderWindow.xaml" })
        {
            var source = ReadAppFile(file);
            Assert.DoesNotContain("立即执行", source);
            Assert.DoesNotContain("立即关机", source);
        }
    }

    // ---- 7. PowerAction 映射 ----

    [Theory]
    [InlineData(PowerAction.Shutdown, "关机")]
    [InlineData(PowerAction.Restart, "重启")]
    [InlineData(PowerAction.Sleep, "睡眠")]
    [InlineData(PowerAction.Hibernate, "休眠")]
    [InlineData(PowerAction.Unknown, "未知状态")]
    public void UiTextMapper_PowerActionMapping(PowerAction action, string expected)
    {
        Assert.Equal(expected, UiTextMapper.Map(action));
    }

    [Fact]
    public void UiTextMapper_UndefinedPowerAction_IsUnknown()
    {
        Assert.Equal("未知状态", UiTextMapper.Map((PowerAction)99));
    }

    // ---- 8. TaskKind 构造互斥 ----

    [Fact]
    public async Task Create_CountdownDefinition_SetsDurationAndNoTimeOfDay()
    {
        var engine = CreateRunningEngine();
        var viewModel = CreateViewModel(engine);
        await viewModel.InitializeAsync();
        viewModel.CountdownHoursText = "1";
        viewModel.CountdownMinutesText = "30";
        viewModel.CountdownSecondsText = "0";

        await viewModel.CreateCommand.ExecuteAsync();

        var command = Assert.Single(engine.Commands.OfType<CreateTaskCommand>());
        Assert.Equal(TaskKind.Countdown, command.Definition.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), command.Definition.CountdownDuration);
        Assert.Null(command.Definition.TargetTimeOfDay);
        Assert.Equal(PowerAction.Shutdown, command.Definition.Action);
    }

    [Fact]
    public async Task Create_TodayAtDefinition_SetsTimeOfDayAndNoDuration()
    {
        var engine = CreateRunningEngine();
        var viewModel = CreateViewModel(engine);
        await viewModel.InitializeAsync();
        viewModel.ModeIsTodayAt = true;
        viewModel.TimeHoursText = "14";
        viewModel.TimeMinutesText = "5";
        viewModel.TimeSecondsText = "0";

        await viewModel.CreateCommand.ExecuteAsync();

        var command = Assert.Single(engine.Commands.OfType<CreateTaskCommand>());
        Assert.Equal(TaskKind.TodayAt, command.Definition.Kind);
        Assert.Null(command.Definition.CountdownDuration);
        Assert.Equal(new TimeOnly(14, 5, 0), command.Definition.TargetTimeOfDay);
    }

    // ---- 9. 倒计时校验 ----

    [Theory]
    [InlineData("0", "0", "0")]
    [InlineData("-1", "0", "0")]
    [InlineData("1", "60", "0")]
    [InlineData("1", "0", "60")]
    [InlineData("169", "0", "0")]
    public async Task Create_InvalidCountdown_IsRejected(string hours, string minutes, string seconds)
    {
        var engine = CreateRunningEngine();
        var viewModel = CreateViewModel(engine);
        await viewModel.InitializeAsync();
        viewModel.CountdownHoursText = hours;
        viewModel.CountdownMinutesText = minutes;
        viewModel.CountdownSecondsText = seconds;

        Assert.False(viewModel.CreateCommand.CanExecute(null));
        await viewModel.CreateCommand.ExecuteAsync();

        Assert.Empty(engine.Commands);
    }

    // ---- 10. 创建按钮禁用条件 ----

    [Fact]
    public async Task Create_WhenEngineNotRunning_IsDisabled()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Faulted, FaultMessage = "boom" }
        };
        var viewModel = CreateViewModel(engine);
        await viewModel.InitializeAsync();

        Assert.False(viewModel.CreateCommand.CanExecute(null));
        Assert.Contains("调度服务未运行", viewModel.CreateDisabledReason);
    }

    [Fact]
    public async Task Create_WhenConfigurationFails_IsDisabled()
    {
        var engine = CreateRunningEngine();
        var config = new FakeConfigurationService(
            new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Corrupt,
                Errors = ["corrupt"]
            });
        var viewModel = new MainWindowViewModel(engine, config, new FakeClock(Now), new NullLogger(), new FakeAutoStartService());
        await viewModel.InitializeAsync();

        Assert.False(viewModel.CreateCommand.CanExecute(null));
        Assert.Contains("配置不可用", viewModel.CreateDisabledReason);
    }

    [Fact]
    public async Task Create_WhenActiveTaskExists_IsDisabled()
    {
        var engine = CreateRunningEngine(
            ScheduledInstance());
        var viewModel = CreateViewModel(engine);
        await viewModel.InitializeAsync();

        Assert.False(viewModel.CreateCommand.CanExecute(null));
        Assert.Contains("已有活动任务", viewModel.CreateDisabledReason);
    }

    // ---- 11. Snooze 使用当前身份 ----

    [Fact]
    public async Task Snooze_UsesCurrentInstanceIdAndStageToken()
    {
        var instance = WarningInstance();
        var engine = CreateRunningEngine(instance);
        var viewModel = CreateViewModel(engine);
        await viewModel.InitializeAsync();

        await viewModel.SnoozeCommand.ExecuteAsync();

        var command = Assert.Single(engine.Commands.OfType<SnoozeTaskCommand>());
        Assert.Equal(instance.InstanceId, command.ExpectedInstanceId);
        Assert.Equal(instance.StageToken, command.ExpectedStageToken);
        Assert.Equal(TimeSpan.FromMinutes(10), command.Duration);
    }

    [Fact]
    public void CancelAndSnoozeCommands_UseCurrentIdentity_InSource()
    {
        var source = ReadAppFile("Presentation", "MainWindowViewModel.cs");

        Assert.Contains("new SnoozeTaskCommand(instance.InstanceId, instance.StageToken", source);
        Assert.Contains("new CancelTaskCommand(instance.InstanceId, instance.StageToken)", source);
    }

    // ---- 12 / 13. 提醒去重与关闭 ----

    [Fact]
    public void NotificationCoordinator_SameWarning_ShownOnce()
    {
        var engine = CreateRunningEngine(WarningInstance());
        var notifications = new FakeNotificationService();
        var coordinator = new NotificationCoordinator(engine, notifications, new NullLogger());

        coordinator.CheckNow();
        coordinator.CheckNow();

        Assert.Single(notifications.Shown);
    }

    [Fact]
    public void NotificationCoordinator_TokenChange_ClosesOldAndShowsNewOnce()
    {
        var instance = WarningInstance();
        var engine = CreateRunningEngine(instance);
        var notifications = new FakeNotificationService();
        var coordinator = new NotificationCoordinator(engine, notifications, new NullLogger());

        coordinator.CheckNow();
        Assert.Single(notifications.Shown);

        engine.Snapshot = engine.Snapshot with
        {
            Instances = new Dictionary<Guid, TaskInstance>
            {
                [instance.SourceTaskId] = instance with { StageToken = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd") }
            }
        };
        coordinator.CheckNow();

        Assert.Contains(instance.InstanceId, notifications.Closed);
        Assert.Equal(2, notifications.Shown.Count);

        // 状态离开 Confirming → 关闭
        engine.Snapshot = engine.Snapshot with
        {
            Instances = new Dictionary<Guid, TaskInstance>
            {
                [instance.SourceTaskId] = instance with { StageToken = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), State = TaskInstanceState.Waiting }
            }
        };
        coordinator.CheckNow();

        Assert.Equal(2, notifications.Closed.Count);
        Assert.Equal(2, notifications.Shown.Count);
    }

    // ---- 14. 我知道了不提交命令 ----

    [Fact]
    public void ReminderDismiss_DoesNotSubmitAnyCommand()
    {
        var source = ReadAppFile("ReminderWindow.xaml.cs");

        Assert.Contains("private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();", source);
    }

    // ---- 16. 刷新只读快照 ----

    [Fact]
    public void RefreshService_OnlyReadsSnapshot()
    {
        var source = ReadAppFile("Presentation", "DashboardRefreshService.cs");

        Assert.Contains("GetSnapshot", source);
        Assert.DoesNotContain("WriteAsync", source);
        Assert.DoesNotContain("runtime.json", source);
        Assert.DoesNotContain("IStorage", source);
        Assert.DoesNotContain("SubmitAsync", source);
    }

    // ---- 17. 唯一主窗口 ----

    [Fact]
    public void WindowActivationService_KeepsSingleMainWindow_ThroughFactory()
    {
        var source = ReadAppFile("Infrastructure", "WindowActivationService.cs");

        Assert.Contains("IMainWindowFactory", source);
        Assert.Contains("_mainWindow ??=", source);
    }

    // ---- 18. 关闭主窗口仍隐藏 ----

    [Fact]
    public void MainWindowClose_HidesInsteadOfExiting()
    {
        var source = ReadAppFile("MainWindow.xaml.cs");

        Assert.Contains("e.Cancel = true", source);
        Assert.Contains("Hide()", source);
        Assert.Contains("IsExiting", source);
    }

    // ---- 19. RunAsync 唯一调用位置 ----

    [Fact]
    public void SchedulerEngineRunAsync_HasSingleCallSite()
    {
        var matches = 0;
        foreach (var file in EnumerateAppFiles("*.cs"))
        {
            var content = File.ReadAllText(file);
            matches += System.Text.RegularExpressions.Regex.Matches(content, @"RunAsync\s*\(").Count;
        }

        Assert.Equal(1, matches);
    }

    // ---- 20. 资源字典存在（XAML 编译由 GPT build 验收） ----

    [Fact]
    public void ThemeResourceDictionaries_ExistAndAreNonEmpty()
    {
        var colors = ReadAppFile("Themes", "Colors.xaml");
        var controls = ReadAppFile("Themes", "Controls.xaml");

        Assert.Contains("ResourceDictionary", colors);
        Assert.Contains("ResourceDictionary", controls);
        Assert.NotEmpty(colors);
        Assert.NotEmpty(controls);
    }

    // ---- Helpers ----

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel CreateViewModel(FakeSchedulerEngine engine)
    {
        var config = new FakeConfigurationService(new ConfigurationLoadResult
        {
            Status = ConfigurationLoadStatus.Success,
            Config = new AppConfig
            {
                SchemaVersion = 1,
                TestMode = true,
                AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
            }
        });

        return new MainWindowViewModel(engine, config, new FakeClock(Now), new NullLogger(), new FakeAutoStartService());
    }

    private static FakeSchedulerEngine CreateRunningEngine(TaskInstance? instance = null)
        => new()
        {
            Snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                Instances = instance is null
                    ? new Dictionary<Guid, TaskInstance>()
                    : new Dictionary<Guid, TaskInstance> { [instance.SourceTaskId] = instance },
                LastUpdatedAt = Now
            }
        };

    private static TaskInstance WarningInstance() => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Confirming,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = new DateTimeOffset(2024, 1, 15, 11, 59, 0, TimeSpan.Zero),
        StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        HasExecuted = false,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static TaskInstance ScheduledInstance() => WarningInstance() with { State = TaskInstanceState.Waiting };

    private static string ReadAppFile(params string[] relativeParts)
    {
        var parts = new[] { FindAppSourceRoot() }.Concat(relativeParts).ToArray();
        return File.ReadAllText(Path.Combine(parts));
    }

    private static IEnumerable<string> EnumerateAppFiles(string pattern)
        => Directory.GetFiles(FindAppSourceRoot(), pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateWholeSourceFiles()
    {
        var root = FindAppSourceRoot();
        var coreRoot = Path.GetFullPath(Path.Combine(root, "..", "AutoShutdown.Core"));
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindAppSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown.App source directory was not found.");
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public List<SchedulerCommand> Commands { get; } = new();

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
        }

        public SchedulerSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class FakeConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public FakeConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<TaskInstance> Shown { get; } = new();

        public List<Guid> Closed { get; } = new();

        public void ShowReminder(TaskInstance instance) => Shown.Add(instance);

        public void CloseReminder(Guid instanceId) => Closed.Add(instanceId);
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public AutoStartStatus Status { get; set; } = AutoStartStatus.Disabled;

        public AutoStartStatus GetStatus() => Status;

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
