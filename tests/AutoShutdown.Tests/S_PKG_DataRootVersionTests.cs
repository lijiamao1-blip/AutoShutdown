using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG 数据根覆盖与版本信息入口测试。
///  - DataRootResolver：默认 %LocalAppData%\AutoShutdown；AUTOSHUTDOWN_DATA_ROOT
///    指向隔离沙箱；空/空白回退默认（绝不静默换目录）。
///  - MainWindowViewModel.VersionText：读取程序集 InformationalVersion 并裁掉 SDK
///    附加的 +{commit} 后缀，与 manifest / Git 提交 / SHA-256 对应。
///  - App 启动接线：App.xaml.cs 必须把 DataRootResolver 的结果传给 DI。
/// 纯内存；不启动应用、不写真实数据目录、不触碰注册表/防火墙/任务计划。
/// </summary>
public sealed class S_PKG_DataRootVersionTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    // ---- DataRootResolver ----

    [Fact]
    public void DataRootResolver_Default_IsLocalAppDataAutoShutdown()
    {
        var saved = SaveEnv();
        try
        {
            Environment.SetEnvironmentVariable(DataRootResolver.EnvironmentVariable, null);

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoShutdown");
            Assert.Equal(expected, DataRootResolver.Resolve());
        }
        finally
        {
            RestoreEnv(saved);
        }
    }

    [Fact]
    public void DataRootResolver_WithEnvVar_ReturnsConfiguredRoot()
    {
        var saved = SaveEnv();
        try
        {
            var sandbox = Path.Combine(Path.GetTempPath(), "as-spkg-sandbox", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(DataRootResolver.EnvironmentVariable, sandbox);

            Assert.Equal(Path.GetFullPath(sandbox), DataRootResolver.Resolve());
        }
        finally
        {
            RestoreEnv(saved);
        }
    }

    [Fact]
    public void DataRootResolver_WithBlankEnvVar_FallsBackToDefault()
    {
        var saved = SaveEnv();
        try
        {
            Environment.SetEnvironmentVariable(DataRootResolver.EnvironmentVariable, "   ");

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoShutdown");
            Assert.Equal(expected, DataRootResolver.Resolve());
        }
        finally
        {
            RestoreEnv(saved);
        }
    }

    // ---- VersionText ----

    [Fact]
    public void VersionText_IsDerivedFromInformationalVersion_TrimmedAtPlus()
    {
        var viewModel = CreateViewModel();
        var informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(MainWindowViewModel).Assembly)
            ?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(viewModel.VersionText));
        Assert.StartsWith("v", viewModel.VersionText);

        // InformationalVersion 形如 vX.Y.Z[+hash]：VersionText 必须去掉 SDK 附加的 +hash，
        // 保留 manifest 可追溯版本串，且不能是空串。
        if (informational is not null && informational.IndexOf('+') >= 0)
        {
            Assert.Equal("v" + informational[..informational.IndexOf('+')].TrimStart('v'), viewModel.VersionText);
        }
    }

    // ---- App 启动接线（源码契约） ----

    [Fact]
    public void AppStartup_PassesDataRootToServiceRegistration()
    {
        var source = ReadAppFile("App.xaml.cs");

        Assert.Contains("DataRootResolver.Resolve()", source);
        Assert.Contains("AddAutoShutdownServices(dataRoot)", source);
        // 不得在启动路径硬编码 LocalAppData 绕过解析器（隔离沙箱能力必须生效）。
        Assert.DoesNotContain("AddAutoShutdownServices()", source);
    }

    // ---- Helpers ----

    private static string? SaveEnv()
        => Environment.GetEnvironmentVariable(DataRootResolver.EnvironmentVariable);

    private static void RestoreEnv(string? saved)
        => Environment.SetEnvironmentVariable(DataRootResolver.EnvironmentVariable, saved);

    private static MainWindowViewModel CreateViewModel()
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

        return new MainWindowViewModel(
            new FakeSchedulerEngine(),
            config,
            new FakeClock(Now),
            new NullLogger(),
            new FakeAutoStartService());
    }

    private static string ReadAppFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown.App source was not found.");
    }

    // ---- 内联最小 fake（与 S13_T08 同款模式；S11 的 fake 是 private，不可跨文件复用）----

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public List<SchedulerCommand> Commands { get; } = new();

        public SchedulerSnapshot GetSnapshot() => Snapshot;

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
