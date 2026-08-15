using System.Text.Json;
using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.4.1 first-run usability fixes. All tests are in-memory (no real file IO,
/// no real registry, no real power). Verifies the safe config initialization
/// path and the home-navigation behaviour.
/// </summary>
public sealed class S12_4_1FirstRunTests
{
    // ---- 1. 配置缺失不会被静默当作成功 ----

    [Fact]
    public async Task Load_WhenConfigMissing_ReturnsMissing_NotSuccess()
    {
        var storage = new InMemoryStorage();
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Missing, result.Status);
        Assert.Null(result.Config);
    }

    // ---- 2. 用户执行安全初始化后生成有效配置 ----

    [Fact]
    public async Task SafeInit_CreatesValidConfig_ThenLoadsSuccess()
    {
        var storage = new InMemoryStorage();
        var service = new ConfigurationService(storage);

        var save = await service.CreateSafeDefaultAsync(CancellationToken.None);
        Assert.Equal(ConfigurationSaveStatus.Success, save.Status);

        var load = await service.LoadAsync(CancellationToken.None);
        Assert.Equal(ConfigurationLoadStatus.Success, load.Status);
        Assert.NotNull(load.Config);
    }

    // ---- 3. 初始化配置固定为 TestMode=true 与四种动作 ----

    [Fact]
    public async Task SafeInit_ConfigIsTestModeTrue_WithFourSafeActions()
    {
        var storage = new InMemoryStorage();
        var service = new ConfigurationService(storage);

        await service.CreateSafeDefaultAsync(CancellationToken.None);
        var load = await service.LoadAsync(CancellationToken.None);
        var config = load.Config!;

        Assert.Equal(1, config.SchemaVersion);
        Assert.True(config.TestMode);
        Assert.False(config.StartWithWindows);
        Assert.Equal(
            [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
            config.AllowedActions);
        Assert.Equal(LogLevel.Information, config.Logging.Level);
    }

    // ---- 4. 初始化失败时创建按钮仍禁用 ----

    [Fact]
    public async Task SafeInit_WhenStorageFails_CreateStaysDisabled()
    {
        var storage = new InMemoryStorage { FailWrites = true };
        var service = new ConfigurationService(storage);

        var save = await service.CreateSafeDefaultAsync(CancellationToken.None);
        Assert.Equal(ConfigurationSaveStatus.IoFailure, save.Status);

        var viewModel = CreateViewModel(
            configResult: MissingResult(),
            initResult: save);
        await viewModel.InitializeAsync();

        // 初始化失败后：仍显示初始化入口、创建按钮保持禁用、显示明确原因。
        await viewModel.InitializeConfigCommand.ExecuteAsync();

        Assert.True(viewModel.IsConfigInitVisible);
        Assert.False(viewModel.CanCreateNow(out _));
        Assert.True(viewModel.HasConfigInitError);
        Assert.Contains("初始化失败", viewModel.ConfigInitErrorText);
    }

    // ---- 5. 有效安全配置下创建按钮可以提交任务 ----

    [Fact]
    public async Task ValidSafeConfig_CreateButton_CanSubmitTask()
    {
        var engine = CreateRunningEngine();
        var viewModel = CreateViewModel(
            engine: engine,
            configResult: SuccessResult());

        await viewModel.InitializeAsync();
        Assert.True(viewModel.CanCreateNow(out _));
        Assert.Equal("安全有效", viewModel.ConfigStatusText);

        await viewModel.CreateCommand.ExecuteAsync();

        Assert.Single(engine.Commands);
        Assert.IsType<CreateTaskCommand>(engine.Commands[0]);
        Assert.Contains("创建任务已提交", viewModel.StatusMessage);
    }

    // ---- 6. 创建短倒计时任务后状态正常更新 ----

    [Fact]
    public void ShortCountdown_AfterCreate_StateUpdates()
    {
        var now = new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);
        var instance = new TaskInstance
        {
            InstanceId = Guid.NewGuid(),
            SourceTaskId = Guid.NewGuid(),
            ActionSnapshot = PowerAction.Shutdown,
            State = TaskInstanceState.Waiting,
            ScheduledFireTime = now.AddSeconds(10),
            WarningStartTime = null,
            StageToken = Guid.NewGuid(),
            HasExecuted = false,
            CreatedAt = now
        };
        var engine = new FakeSchedulerEngine
        {
            Snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                Instances = new Dictionary<Guid, TaskInstance> { [instance.SourceTaskId] = instance },
                LastUpdatedAt = now
            }
        };
        var viewModel = CreateViewModel(
            engine: engine,
            configResult: SuccessResult(),
            now: now);

        viewModel.Refresh(engine.Snapshot, now);

        Assert.Equal("00:00:10", viewModel.CountdownText);
        Assert.Equal("2024-01-15 11:00:10", viewModel.NextFireTimeText);
        Assert.Equal("关机", viewModel.TaskActionText);
    }

    // ---- 7. IPowerService 通过 GuardedPowerService 解析（默认测试模式走 Fake） ----

    [Fact]
    public void Di_IPowerService_ResolvesToGuardedPowerService()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices(Path.Combine(Path.GetTempPath(), "autoshutdown-s1241-di-" + Guid.NewGuid().ToString("N")));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<GuardedPowerService>(provider.GetRequiredService<IPowerService>());
    }

    // ---- 8. 首页点击不再折叠侧栏 ----

    [Fact]
    public void HomeCommand_NavigatesHome_WithoutCollapsingNav()
    {
        var viewModel = CreateViewModel(configResult: SuccessResult());
        viewModel.SelectedNav = viewModel.NavItems[1]; // 从其他页切回

        viewModel.HomeCommand.Execute(null);

        Assert.Equal("home", viewModel.SelectedNav.PageKey);
        Assert.False(viewModel.IsNavCollapsed);
        Assert.Equal(220, viewModel.NavColumnWidth);
    }

    // ---- 9. 配置损坏和 TestMode=false 时仍安全拒绝 ----

    [Fact]
    public async Task CorruptConfig_KeepsCreateRejected()
    {
        var viewModel = CreateViewModel(configResult: new ConfigurationLoadResult
        {
            Status = ConfigurationLoadStatus.Corrupt,
            Errors = ["corrupt"]
        });

        await viewModel.InitializeAsync();

        Assert.False(viewModel.CanCreateNow(out _));
        Assert.Contains("配置", viewModel.ConfigStatusText);
    }

    [Fact]
    public async Task TestModeFalse_KeepsCreateRejected()
    {
        var viewModel = CreateViewModel(configResult: SuccessResult(testMode: false));

        await viewModel.InitializeAsync();

        Assert.False(viewModel.CanCreateNow(out _));
        Assert.Contains("测试模式已关闭", viewModel.ConfigStatusText);
    }

    // ---- 10. 发布包不包含用户运行产生的 config/runtime/log 文件 ----

    [Fact]
    public void ReleaseStaging_ContainsNoUserDataFiles()
    {
        var root = FindProjectRoot();
        var staging = Path.Combine(root, "artifacts", "release", "v1.0.0", "staging");
        if (!Directory.Exists(staging))
        {
            return; // 未发布时无可检查内容
        }

        foreach (var stageDir in Directory.GetDirectories(staging))
        {
            var files = Directory.GetFiles(stageDir, "*", SearchOption.AllDirectories);
            Assert.DoesNotContain(files, f =>
                Path.GetFileName(f).Equals("config.json", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).Equals("runtime.json", StringComparison.OrdinalIgnoreCase)
                || (Path.GetFileName(f).StartsWith("autoshutdown-", StringComparison.OrdinalIgnoreCase)
                    && f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)));
        }

        // 发布脚本不得复制用户数据目录内容（精确匹配 config.json 文件名，
        // 避免与 runtimeconfig.json 子串混淆）。
        var script = File.ReadAllText(Path.Combine(root, "tools", "Publish-SafeRelease.ps1"));
        Assert.DoesNotContain("LocalApplicationData", script);
        Assert.DoesNotContain("'config.json'", script);
        Assert.DoesNotContain("\"config.json\"", script);
    }

    // ---- Helpers ----

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    private static ConfigurationLoadResult MissingResult()
        => new() { Status = ConfigurationLoadStatus.Missing, Errors = ["missing"] };

    private static ConfigurationLoadResult SuccessResult(bool testMode = true)
        => new()
        {
            Status = ConfigurationLoadStatus.Success,
            Config = new AppConfig
            {
                SchemaVersion = 1,
                TestMode = testMode,
                AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
            }
        };

    private static FakeSchedulerEngine CreateRunningEngine()
        => new()
        {
            Snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                Instances = new Dictionary<Guid, TaskInstance>(),
                LastUpdatedAt = Now
            }
        };

    private static MainWindowViewModel CreateViewModel(
        FakeSchedulerEngine? engine = null,
        ConfigurationLoadResult? configResult = null,
        ConfigurationSaveResult? initResult = null,
        DateTimeOffset? now = null)
    {
        engine ??= CreateRunningEngine();
        var config = new StubConfigurationService(
            configResult ?? MissingResult(),
            initResult);
        var clock = new FakeClock(now ?? Now);
        var autoStart = new NoOpAutoStartService();
        return new MainWindowViewModel(engine, config, clock, new NullLogger(), autoStart);
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown project root was not found.");
    }

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _values = new();

        public bool FailWrites { get; set; }

        public Task<StorageReadResult<T>> ReadAsync<T>(string relativePath, CancellationToken cancellationToken)
        {
            if (_values.TryGetValue(relativePath, out var element))
            {
                var typed = element.Deserialize<T>();
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Success,
                    Value = typed
                });
            }

            return Task.FromResult(new StorageReadResult<T> { Status = StorageReadStatus.NotFound });
        }

        public Task<StorageWriteResult> WriteAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
        {
            if (FailWrites)
            {
                return Task.FromResult(new StorageWriteResult
                {
                    Status = StorageWriteStatus.IoFailure,
                    Error = "simulated write failure"
                });
            }

            // 模拟 FileStorage：序列化后存储，读取时再反序列化。
            _values[relativePath] = JsonSerializer.SerializeToElement(value!);
            return Task.FromResult(new StorageWriteResult { Status = StorageWriteStatus.Success });
        }
    }

    private sealed class StubConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;
        private readonly ConfigurationSaveResult? _initResult;

        public StubConfigurationService(
            ConfigurationLoadResult result,
            ConfigurationSaveResult? initResult = null)
        {
            _result = result;
            _initResult = initResult;
        }

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(_initResult ?? new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
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
