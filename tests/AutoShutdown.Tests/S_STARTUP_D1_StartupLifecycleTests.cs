using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-STARTUP-D1 聚焦回归测试。覆盖：
/// (1) DI 构造阶段不再有无界同步文件等待（根因回归——用「读取永久挂起」的存储证明工厂不触碰存储）；
/// (2) 应用生命周期协调器在启动前可从组合根有界解析；
/// (3) 启动超时卫兵在启动卡死时有限时间非零退出；正常完成则不误触发；
/// (4) 单实例所有权在失败清理后可被下一实例立即获取；
/// (5) 激活管道能把 ACTIVATE 转发给窗口激活服务并应答 OK；
/// (6) Task Scheduler 同步开关读取有界且 fail-closed（挂起/损坏一律关闭）。
/// 全部测试只读/内存，不触碰真实电源、远程监听或系统任务计划。
/// </summary>
public sealed class S_STARTUP_D1_StartupLifecycleTests
{
    // ===== (1) DI 工厂根因回归：不得在构造期同步读取 task-sync.json =====

    [Fact]
    public async Task TaskSyncCoordinatorFactory_WithHangingSettingsStore_ResolvesBounded_AndFailClosed()
    {
        var root = NewTempRoot();
        try
        {
            var services = new ServiceCollection();
            services.AddAutoShutdownServices(root);
            // 用「读取永久挂起」的存储覆盖注册：若工厂回归为构造期同步
            // LoadAsync().GetResult()，本次解析会在限时内失败；修复后工厂不触碰存储，
            // 解析立即完成，且保持 fail-closed 默认关闭。
            services.AddSingleton<TaskSyncSettingsStore>(
                new TaskSyncSettingsStore(new HangingStorage()));
            await using var provider = services.BuildServiceProvider();

            var resolveTask = Task.Run(
                () => provider.GetRequiredService<TaskSyncCoordinator>());
            var completed = await Task.WhenAny(
                resolveTask,
                Task.Delay(TimeSpan.FromSeconds(10))) == resolveTask;

            Assert.True(completed, "TaskSyncCoordinator 解析不得在构造期同步读取（挂起）存储。");
            var coordinator = await resolveTask;
            Assert.NotNull(coordinator);
            Assert.False(coordinator.Enabled, "fail-closed：默认必须保持任务计划同步关闭。");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ===== (2) 协调器在启动前可从组合根解析（有界） =====

    [Fact]
    public void CompositionRoot_ResolvesApplicationLifetimeCoordinator_WithoutBlocking()
    {
        var root = NewTempRoot();
        try
        {
            var elapsed = RunSta(() =>
            {
                // DashboardRefreshService 依赖 Application.Current.Dispatcher，
                // 协调器解析必须运行在 STA 线程并持有 WPF Application（同真实启动形态）。
                var app = new System.Windows.Application();
                try
                {
                    var services = new ServiceCollection();
                    services.AddAutoShutdownServices(root);
                    services.AddSingleton(new SingleInstanceCoordinator());
                    var provider = services.BuildServiceProvider();
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var coordinator = provider.GetRequiredService<ApplicationLifetimeCoordinator>();
                        sw.Stop();

                        Assert.NotNull(coordinator);
                        return sw.Elapsed;
                    }
                    finally
                    {
                        // ActivationPipeServer 只实现 IAsyncDisposable：容器须走异步释放（同 App.xaml.cs）。
                        provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    app.Dispatcher.InvokeShutdown();
                }
            });

            Assert.True(
                elapsed < TimeSpan.FromSeconds(15),
                "协调器解析应在有界时间内完成（实测 " + elapsed.TotalSeconds + " 秒）。");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ===== (3) 启动超时卫兵 =====

    [Fact]
    public void StartupTimeoutGuard_Fires_ExitsNonZero_WhenStartupNeverCompletes()
    {
        var logger = new RecordingLogger();
        var exitCodes = new List<int>();
        using var guard = new StartupTimeoutGuard(
            logger,
            exitCode =>
            {
                lock (exitCodes)
                {
                    exitCodes.Add(exitCode);
                }
            },
            TimeSpan.FromMilliseconds(200));

        guard.Arm();
        Thread.Sleep(1500);
        guard.Disarm();

        lock (exitCodes)
        {
            Assert.Equal(new[] { StartupTimeoutGuard.TimeoutExitCode }, exitCodes);
        }

        Assert.Contains(logger.Entries, entry => entry == "StartupTimedOut");
    }

    [Fact]
    public void StartupTimeoutGuard_DoesNotFire_WhenDisarmedBeforeDeadline()
    {
        var logger = new RecordingLogger();
        var exitCodes = new List<int>();
        using var guard = new StartupTimeoutGuard(
            logger,
            exitCode =>
            {
                lock (exitCodes)
                {
                    exitCodes.Add(exitCode);
                }
            },
            TimeSpan.FromMilliseconds(300));

        guard.Arm();
        Thread.Sleep(50);
        guard.Disarm();
        Thread.Sleep(600);

        lock (exitCodes)
        {
            Assert.Empty(exitCodes);
        }

        Assert.DoesNotContain(logger.Entries, entry => entry == "StartupTimedOut");
    }

    // ===== (4) 单实例所有权：失败清理释放后可被下一实例获取 =====

    [Fact]
    public void SingleInstance_MutexReleasedOnFailureCleanup_NextInstanceBecomesPrimary()
    {
        // 模拟「主实例关键初始化失败后释放互斥体」：进程退出时 OS 同样会释放命名单实例
        // 互斥体；此处直接验证释放后下一实例可立即成为主实例（不留任何后台占用）。
        using var first = new SingleInstanceCoordinator();
        var acquired = first.TryAcquirePrimary();
        Assert.Equal(SingleInstanceResult.Primary, acquired.Result);

        first.Dispose();

        using var second = new SingleInstanceCoordinator();
        var next = second.TryAcquirePrimary();
        Assert.Equal(SingleInstanceResult.Primary, next.Result);
    }

    // ===== (5) 激活管道：ACTIVATE 转发并应答 OK =====

    [Fact]
    public async Task ActivationPipe_ForwardsActivate_AndAcknowledgesOk()
    {
        var activation = new RecordingWindowActivation();
        var pipeName = @"AutoShutdown.Activation.Test." + Guid.NewGuid().ToString("N");
        var server = new ActivationPipeServer(activation, pipeName: pipeName);
        server.Start();

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var request = Encoding.UTF8.GetBytes("ACTIVATE\n");
            await client.WriteAsync(request, 0, request.Length, CancellationToken.None);
            await client.FlushAsync(CancellationToken.None);

            var response = await ReadLineAsync(client);
            Assert.Equal("OK", response);
            Assert.True(activation.Activated, "ACTIVATE 必须被转发到窗口激活服务。");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ===== (6) Task Scheduler 同步开关：有界 + fail-closed =====

    [Fact]
    public void StartupTaskSyncSettingsGate_IsBounded_AndFailClosedOnHang()
    {
        var logger = new RecordingLogger();
        var store = new TaskSyncSettingsStore(new HangingStorage());

        var sw = Stopwatch.StartNew();
        var enabled = StartupTaskSyncSettingsGate.LoadEnabled(
            store, logger, TimeSpan.FromMilliseconds(200));
        sw.Stop();

        Assert.False(enabled, "挂起存储必须 fail-closed 返回关闭。");
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            "有界装载必须在超时附近返回，不得无限等待（实测 " + sw.Elapsed.TotalSeconds + " 秒）。");
        Assert.Contains(logger.Entries, entry => entry == "TaskSyncSettingsLoadFailed");
    }

    [Fact]
    public void StartupTaskSyncSettingsGate_LoadsEnabledFromFile_AndFailClosedOnCorrupt()
    {
        var root = NewTempRoot();
        try
        {
            var logger = new RecordingLogger();

            // 首次使用（无文件）→ NotFound → 关闭。
            var emptyStore = new TaskSyncSettingsStore(new FileStorage(root));
            Assert.False(StartupTaskSyncSettingsGate.LoadEnabled(
                emptyStore, logger, TimeSpan.FromSeconds(5)));

            // 显式启用 → Success + Enabled → 打开（启动步骤恢复开关）。
            var fileStore = new TaskSyncSettingsStore(new FileStorage(root));
            var save = BlockingSave(fileStore, new TaskSyncSettingsDocument { Enabled = true });
            Assert.True(save.Succeeded);
            Assert.True(StartupTaskSyncSettingsGate.LoadEnabled(
                fileStore, logger, TimeSpan.FromSeconds(5)));

            // 损坏 → 保持关闭（fail-closed，绝不静默启用）。
            File.WriteAllText(
                Path.Combine(root, TaskSyncSettingsStore.FileName),
                "{ not-json");
            var corruptStore = new TaskSyncSettingsStore(new FileStorage(root));
            Assert.False(StartupTaskSyncSettingsGate.LoadEnabled(
                corruptStore, logger, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ===== (7) 源码契约：检查点日志 + 无构造期同步读取 + 干净非零失败 =====

    [Fact]
    public void TaskSyncCoordinatorFactory_DoesNotSyncLoadSettings_InSource()
    {
        var source = ReadAppFile("AppHost", "ServiceRegistration.cs");
        var start = source.IndexOf(
            "services.AddSingleton<TaskSyncCoordinator>", StringComparison.Ordinal);
        var end = source.IndexOf(
            "services.AddSingleton<ExternalTriggerScheduleGate>", StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "未找到 TaskSyncCoordinator 工厂注册段。");
        var factory = source[start..end];
        Assert.DoesNotContain("GetResult", factory);
        Assert.DoesNotContain("LoadAsync", factory);
        Assert.Contains("coordinator.Enabled = false", factory);
    }

    [Fact]
    public void AppStartup_HasCheckpointLogs_AndCleanNonZeroFailure()
    {
        var app = ReadAppFile("App.xaml.cs");
        foreach (var checkpoint in new[]
                 {
                     "PrimaryInstanceAcquired",
                     "ServiceProviderBuilt",
                     "LifetimeCoordinatorResolving",
                     "LifetimeCoordinatorResolved"
                 })
        {
            Assert.Contains(checkpoint, app);
        }

        Assert.Contains("StartupTimeoutGuard", app);
        Assert.Contains("StartupFailed", app);
        Assert.Contains("Shutdown(StartupFailureExitCode)", app);

        // S-UI3 不变式必须保持：测试实例拒绝转发发生在 ActivationPipeClient.Try 之前。
        Assert.True(
            app.IndexOf("UiTestExistingInstanceRefused", StringComparison.Ordinal)
            < app.IndexOf("ActivationPipeClient.Try", StringComparison.Ordinal));
    }

    [Fact]
    public void CoordinatorStart_HasCheckpointLogs_AndBoundedStartupSteps()
    {
        var coordinator = ReadAppFile("AppHost", "ApplicationLifetimeCoordinator.cs");
        foreach (var checkpoint in new[]
                 {
                     "ActivationPipeStarting",
                     "ActivationPipeStarted",
                     "SchedulerStarting",
                     "SchedulerRunning",
                     "MainWindowActivating",
                     "MainWindowActivated",
                     "ApplicationStarted"
                 })
        {
            Assert.Contains(checkpoint, coordinator);
        }

        Assert.Contains("StartupTaskSyncSettingsGate.LoadEnabled", coordinator);
        // 有界：启动步骤一律 WaitAsync(StartupStepTimeout) 限时，不再在 UI 线程无限等待。
        Assert.Contains("WaitAsync(StartupStepTimeout)", coordinator);
    }

    // ===== Helpers =====

    private static TaskSyncSettingsSaveResult BlockingSave(
        TaskSyncSettingsStore store,
        TaskSyncSettingsDocument document)
        => store.SaveAsync(document, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<string?> ReadLineAsync(PipeStream pipe)
    {
        var buffer = new byte[64];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await pipe.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            if (read == 0)
            {
                return null;
            }

            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (value == (byte)'\n')
                {
                    return builder.ToString();
                }

                builder.Append((char)value);
            }
        }
    }

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(completed, "STA 线程未在 30 秒内完成（协调器解析挂起？）。");

        if (error is not null)
        {
            throw new InvalidOperationException("STA 工作失败：", error);
        }

        return result;
    }

    private static string ReadAppFile(params string[] relativeParts)
    {
        var parts = new[] { FindAppSourceRoot() }
            .Concat(relativeParts)
            .ToArray();
        return File.ReadAllText(Path.Combine(parts));
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

    private static string NewTempRoot()
        => Path.Combine(Path.GetTempPath(), "autoshutdown-startup-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>读取永久挂起：模拟磁盘/IO 停滞时的无界存储读取。</summary>
    private sealed class HangingStorage : IStorage
    {
        public async Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new StorageReadResult<T> { Status = StorageReadStatus.IoFailure };
        }

        public async Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new StorageWriteResult { Status = StorageWriteStatus.IoFailure };
        }
    }

    private sealed class RecordingLogger : IApplicationLogger
    {
        private readonly List<string> _events = new();
        private readonly object _sync = new();

        public string LogDirectory => string.Empty;

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
        {
            lock (_sync)
            {
                _events.Add(eventName);
            }
        }

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingWindowActivation : IWindowActivationService
    {
        public bool Activated { get; private set; }

        public bool IsExiting => false;

        public void ActivateMainWindow() => Activated = true;

        public void MarkExiting()
        {
        }
    }
}
