using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AutoShutdown.App;
using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Remote;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;
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

    // ===== (8) S-STARTUP-D1-D1 实现修正回归 =====
    // 总顾问裁决后新增：远程启动超时不监听；激活管道启动未就绪协议；日志跨进程临界区。

    // --- 缺口 #2：远程启动超时 → 独立可取消 CTS + 确认不监听（即使底层阻塞随后解除） ---

    [Fact]
    public async Task RemoteStart_OnTimeout_EvenIfBlockLaterResolves_NeverListens()
    {
        var root = NewTempRoot();
        try
        {
            var gate = new TaskCompletionSource();
            var settings = JsonSerializer.SerializeToElement(new RemoteSettingsDocument
            {
                Enabled = true,
                ListenAddress = "127.0.0.1",
                ListenPort = 48732,
                RequireTls = false
            });
            var storage = new BlockingRemoteSettingsStorage(gate.Task, settings);

            var services = new ServiceCollection();
            services.AddAutoShutdownServices(root);
            // 覆盖远程设置存储为「读取永久阻塞直到放行」：模拟启动阶段磁盘停滞。
            services.AddSingleton(new RemoteSettingsStore(storage));
            await using var provider = services.BuildServiceProvider();

            var server = provider.GetRequiredService<RemoteServer>();
            var logger = new RecordingLogger();

            // 超时很短：StartBounded 必须在有界时间内返回并确认不监听。
            _ = RemoteStartController.StartBounded(server, logger, CancellationToken.None, TimeSpan.FromMilliseconds(200));

            Assert.False(server.IsRunning, "远程启动超时后必须保持不监听。");
            Assert.Contains(logger.Entries, e => e == "RemoteStartTimedOut");

            // 即使底层阻塞随后解除（磁盘恢复），StartAsync 的取消检查点也保证绝不进入监听。
            gate.SetResult();
            await storage.Released.WaitAsync(TimeSpan.FromSeconds(5));
            // 有界轮询确认：从阻塞返回后的数秒内从未进入监听（fail-closed）。
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                Assert.False(server.IsRunning, "底层阻塞解除后也绝不进入监听（fail-closed）。");
                await Task.Delay(50);
            }
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // --- S-STARTUP-D1-D2：确定性竞态回归——超时已确认停止后，晚到恢复的启动线程绝不发布监听器 ---
    // 覆盖总顾问复验指出的 TOCTOU 竞态：最后一次取消检查已通过、_listener 尚未发布的窗口内，
    // 控制器超时并确认停止后，启动线程晚到恢复也不得再发布监听器（fail-closed，确定性测试门）。

    [Fact]
    public async Task RemoteStart_OnTimeout_PublishGate_AbortedAttemptNeverPublishes_AndResourcesReleased()
    {
        const int port = 48733;
        var root = NewTempRoot();
        try
        {
            var settings = JsonSerializer.SerializeToElement(new RemoteSettingsDocument
            {
                Enabled = true,
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                RequireTls = false
            });

            // 设置读取立即返回（不阻塞）：让启动线程顺利通过取消检查点并创建监听器，
            // 精确停在发布门前（测试门），确定性复现竞态窗口。
            var services = new ServiceCollection();
            services.AddAutoShutdownServices(root);
            services.AddSingleton(new RemoteSettingsStore(
                new BlockingRemoteSettingsStorage(Task.CompletedTask, settings)));
            await using var provider = services.BuildServiceProvider();

            var server = provider.GetRequiredService<RemoteServer>();
            var logger = new RecordingLogger();

            // 测试门：恰在「最后一次取消检查已通过、_listener 尚未发布」处阻塞启动线程。
            var gateEntered = new TaskCompletionSource();
            var releaseStart = new TaskCompletionSource();
            server.BeforePublishAsync = async () =>
            {
                gateEntered.SetResult();
                await releaseStart.Task;
            };

            Task? remoteTask = null;
            try
            {
                // 超时很短：StartBounded 必须超时返回并确认不监听（返回被监督的底层启动任务）。
                remoteTask = RemoteStartController.StartBounded(
                    server, logger, CancellationToken.None, TimeSpan.FromMilliseconds(200));

                // 先确认启动线程已确定性停在发布门前，且控制器已返回并保持不监听。
                await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(server.IsRunning, "控制器返回后必须保持不监听。");
                Assert.Contains(logger.Entries, e => e == "RemoteStartTimedOut");
            }
            finally
            {
                // 再放行启动线程（即使断言失败也不泄漏后台任务）。
                releaseStart.SetResult();
            }

            // 后台启动任务最终结束（有界等待；独立 CTS 由延迟释放收尾）。
            Assert.NotNull(remoteTask);
            await remoteTask!.WaitAsync(TimeSpan.FromSeconds(5));

            // 持续有界检查：IsRunning 始终为 false（超时中止后，晚到恢复的启动线程绝不发布）。
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                Assert.False(server.IsRunning, "超时中止后，晚到恢复的启动线程绝不发布监听器（fail-closed）。");
                await Task.Delay(50);
            }

            // 验证没有监听端口：连接必须被拒绝（监听器已回收）。
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
            });

            // 资源被释放：同一端口可被重新绑定（旧 socket 已关闭回收）。
            using (var probe = new TcpListener(IPAddress.Loopback, port))
            {
                probe.Start();
            }
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // --- 缺口 #3：激活管道启动未就绪协议——UI 线程被启动步骤阻塞时，管道仍快速应答、绝不挂起 ---

    [Fact]
    public async Task ActivationPipe_Activate_WhilePrimaryUiBlocked_RespondsBounded_NoResidual()
    {
        var pipeName = @"AutoShutdown.Activation.Test." + Guid.NewGuid().ToString("N");

        var appCreated = new TaskCompletionSource();
        var releaseUi = new TaskCompletionSource();
        var uiExited = new TaskCompletionSource();
        Exception? uiError = null;
        // 记录工厂 Create 调用：激活「先应答后执行」时，UI 未就绪阶段应为 0。
        var factory = new RecordingMainWindowFactory();

        // STA 线程承载真实 WPF Application：Dispatcher 已创建但「启动步骤」被阻塞（未泵消息），
        // 精确模拟主实例 UI 线程卡在启动步骤时的状态。
        var uiThread = new Thread(() =>
        {
            System.Windows.Application? app = null;
            try
            {
                app = new System.Windows.Application();
                appCreated.SetResult();
                releaseUi.Task.Wait(); // UI 线程阻塞在启动步骤：不泵消息
            }
            catch (Exception exception)
            {
                uiError = exception;
            }
            finally
            {
                try
                {
                    if (app is not null)
                    {
                        // 必须显式关闭 Application：否则 Application.Current 泄漏到后续测试，
                        // 使下一个 new Application() 抛「Cannot create more than one
                        // System.Windows.Application instance in the same AppDomain」。
                        // InvokeShutdown 同步触发 Dispatcher.ShutdownStarted，Application 的
                        // 清理回调随之清空 Application.Current；同时中止排队中的激活延迟执行
                        // 回调（UI 未就绪阶段窗口不会被创建）。绝不能用 app.Shutdown() +
                        // Dispatcher.Run()：Shutdown 只排入 Send 队列且其回调依赖泵消息，
                        // 在当前测试形态下会让 UI 线程永久卡在 Run()（实测超时）。
                        app.Dispatcher.InvokeShutdown();
                    }
                }
                catch (Exception exception)
                {
                    uiError ??= exception;
                }

                uiExited.SetResult();
            }
        })
        {
            IsBackground = true,
            Name = "SStartupD1BlockedUiThread"
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        await appCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(uiError);

        var service = new WindowActivationService(factory);
        var server = new ActivationPipeServer(service, pipeName: pipeName);
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

            // 关键断言：UI 线程被启动步骤阻塞时，管道仍必须快速应答「已接收」——绝不无限等待
            // 被阻塞的 UI 线程（否则次实例会 ActivationForwardFailed / 永久卡住）。
            var responseTask = ReadLineAsync(client);
            var completed = await Task.WhenAny(
                responseTask,
                Task.Delay(TimeSpan.FromSeconds(2))) == responseTask;
            Assert.True(completed, "主实例 UI 线程被阻塞时，激活管道必须在有界时间内应答，不得挂起。");
            Assert.Equal("OK", await responseTask);

            // 先应答后执行：UI 未就绪时激活回调尚未执行（窗口尚未创建）。
            Assert.Equal(0, factory.CreateCount);
        }
        finally
        {
            await server.DisposeAsync();
        }

        // 放行 UI 线程并确认其干净退出（无残留后台线程/进程）。
        releaseUi.SetResult();
        await uiExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(uiError);

        // InvokeShutdown 只关调度器、不清 Application.Current：必须显式清空，否则后续创建
        // Application 的测试（如 CompositionRoot_Resolves…）会抛「Cannot create more than one
        // System.Windows.Application instance in the same AppDomain」。
        ClearApplicationCurrent();
    }

    // --- 缺口 #4：日志跨进程临界区——两进程并发逼近轮转阈值，无覆盖/无丢行/无轮转冲突 ---

    [Fact]
    public async Task FileLogger_TwoConcurrentWriters_ApproachingRotationThreshold_NoOverwriteNoDropNoConflict()
    {
        var root = NewTempRoot();
        try
        {
            // 两个独立 logger 实例模拟两个进程：共享同一命名单实例互斥锁与同一日志目录。
            // 阈值取真实尺寸（每文件约 26 行）：仍反复逼近并跨越轮转阈值（140 行 × ~76B
            // ≈ 10.6KB，将产生约 5 次轮转），但临界区（移动 + 写入 + 落盘）短，绝不触发
            // 500ms 互斥锁超时退化的无锁追加——测试验证的是临界区修复本身（无覆盖/无丢行/
            // 无轮转冲突），而非无锁降级路径（有文档声明的尽力而为语义）。
            using var loggerA = new FileApplicationLogger(
                root,
                minLevel: ApplicationLogLevel.Information,
                maxFileBytes: 2048);
            using var loggerB = new FileApplicationLogger(
                root,
                minLevel: ApplicationLogLevel.Information,
                maxFileBytes: 2048);

            const int linesPerWriter = 70;
            var markers = new ConcurrentBag<string>();

            var writerA = Task.Run(() => WriteMarkedLines(loggerA, "A", linesPerWriter, markers));
            var writerB = Task.Run(() => WriteMarkedLines(loggerB, "B", linesPerWriter, markers));
            await Task.WhenAll(writerA, writerB);

            // 阈值极小，必须发生轮转（否则测试没逼近轮转，无验证意义）。
            Assert.True(
                Directory.GetFiles(root).Any(f => f.EndsWith(".1.log")),
                "逼近轮转阈值应产生轮转文件。");

            var allLines = ReadAllLogLines(root);
            foreach (var marker in markers)
            {
                var matches = allLines.Where(l => l.Contains(marker)).ToArray();
                Assert.True(
                    matches.Length == 1,
                    $"marker {marker} 应恰出现一次，实际 {matches.Length}（覆盖或丢行）。");
                Assert.True(
                    matches[0].EndsWith("#SENTINEL"),
                    $"marker {marker} 所在行必须完整（未被截断/交错）。");
            }

            Assert.Equal(markers.Count, allLines.Count(l => l.Contains("#SENTINEL")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task FileLogger_WhenCrossProcessLockUnavailable_DoesNotRotateLockFree_AndLineNotLost()
    {
        var root = NewTempRoot();
        try
        {
            using var logger = new FileApplicationLogger(
                root,
                minLevel: ApplicationLogLevel.Information,
                maxFileBytes: 50);

            // 模拟另一进程持有跨进程写锁：命名单实例互斥体按线程递归，同一线程 WaitOne 会
            // 直接成功，因此必须用独立线程持有，才能让 logger 的 WaitOne(500ms) 真正超时。
            var lockHeld = new TaskCompletionSource();
            var releaseLock = new TaskCompletionSource();
            var holderExited = new TaskCompletionSource();
            var holder = new Thread(() =>
            {
                try
                {
                    // 同名内核对象已由静态 _writeMutex 创建；对已存在的命名单实例互斥体，
                    // ctor 的 initiallyOwned 无效，必须显式 WaitOne 获取。
                    using var externalLock = new Mutex(
                        initiallyOwned: false,
                        @"Local\AutoShutdown.Desktop.LogWriter.v1");
                    Assert.True(
                        externalLock.WaitOne(TimeSpan.FromSeconds(5)),
                        "外部持有线程必须获得跨进程写锁。");
                    lockHeld.SetResult();
                    releaseLock.Task.Wait(); // 保持持有，直到测试放行
                    externalLock.ReleaseMutex();
                }
                finally
                {
                    holderExited.SetResult();
                }
            })
            {
                IsBackground = true,
                Name = "ExternalLogLockHolder"
            };
            holder.Start();
            await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // 第一行超阈值且锁不可用：必须退化为无锁追加，且绝不无锁 Rotate。
            logger.Log(ApplicationLogLevel.Information, "E", "first-line-payload-#SENTINEL");

            // 锁不可用期间不得创建轮转文件（无锁 Rotate 被禁止）。
            Assert.False(
                Directory.GetFiles(root).Any(f => f.EndsWith(".1.log")),
                "获取跨进程写锁失败时不得无锁 Rotate（否则两进程同时轮转会互相移动对方文件）。");

            // 行不得丢失：base 文件包含第一行。
            Assert.Contains(ReadAllLogLines(root), l => l.Contains("first-line-payload-#SENTINEL"));

            // 放行外部持有线程并确认其已释放锁后，再执行后续写。
            releaseLock.SetResult();
            await holderExited.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // 后续写：锁可用 → 正常触发轮转。
            logger.Log(ApplicationLogLevel.Information, "E", "second-line-payload-#SENTINEL");
            Assert.True(
                Directory.GetFiles(root).Any(f => f.EndsWith(".1.log")),
                "锁可用后应正常轮转。");

            var allAfter = ReadAllLogLines(root);
            Assert.Contains(allAfter, l => l.Contains("first-line-payload-#SENTINEL"));
            Assert.Contains(allAfter, l => l.Contains("second-line-payload-#SENTINEL"));
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

        // STA 线程的 InvokeShutdown 只关调度器、不清 Application.Current；不显式清空会
        // 泄漏到后续创建 Application 的测试（见 ClearApplicationCurrent 注释）。
        ClearApplicationCurrent();

        return result;
    }

    /// <summary>
    /// WPF 不公开 Application.Current 的 setter；Application.Shutdown() 的清理回调依赖泵消息
    /// （阻塞 UI 形态下会让线程挂起），Dispatcher.InvokeShutdown() 只关调度器、不清 Current。
    /// 因此测试直接清空私有静态字段：_appInstance 承载 Current，_appCreatedInThisAppDomain 是
    /// Application 构造器的「只能创建一个」守卫（.NET 8 实测：即使 Current 已为 null，该守卫
    /// 仍为 true 时 new Application() 会抛）。两者都清空，保证创建 Application 的线程退出后
    /// 后续（可能换序运行的）Application 测试可正常创建新实例。
    /// </summary>
    private static void ClearApplicationCurrent()
    {
        var type = typeof(System.Windows.Application);
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

        var instanceField = type.GetField("_appInstance", flags);
        if (instanceField is not null)
        {
            instanceField.SetValue(null, null);
        }

        var guardField = type.GetField("_appCreatedInThisAppDomain", flags);
        if (guardField is not null)
        {
            var defaulted = guardField.FieldType == typeof(bool)
                ? (object)false
                : guardField.FieldType == typeof(int)
                    ? (object)0
                    : null;
            if (defaulted is not null)
            {
                guardField.SetValue(null, defaulted);
            }
        }

        Assert.True(System.Windows.Application.Current is null,
            "ClearApplicationCurrent 后 Application.Current 仍非空。");
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

        public void ActivateMainWindowDeferred() => Activated = true;

        public void MarkExiting()
        {
        }
    }

    private sealed class RecordingMainWindowFactory : IMainWindowFactory
    {
        public int CreateCount { get; private set; }

        public MainWindow Create()
        {
            CreateCount++;
            // 测试下不真正创建主窗口：若激活回调在 UI 就绪前被错误执行，CreateCount 递增，
            // 且返回 null 会在 ActivateMainWindowCore 上暴露（测试失败而非静默通过）。
            return null!;
        }
    }

    /// <summary>
    /// 远程设置读取「永久阻塞直到放行」：模拟启动阶段磁盘停滞。放行后返回有效 enabled 设置，
    /// 用于验证「超时后即使底层阻塞解除也绝不监听」（fail-closed）。
    /// </summary>
    private sealed class BlockingRemoteSettingsStorage : IStorage
    {
        private readonly Task _gate;
        private readonly JsonElement _settings;
        private readonly TaskCompletionSource _released = new();

        public BlockingRemoteSettingsStorage(Task gate, JsonElement settings)
        {
            _gate = gate;
            _settings = settings;
        }

        /// <summary>底层阻塞已解除且读取已返回（供测试有界等待）。</summary>
        public Task Released => _released.Task;

        public async Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            await _gate; // 磁盘停滞：阻塞直到测试放行（忽略取消——正是超时场景的阻塞形态）
            _released.TrySetResult();
            if (typeof(T) == typeof(JsonElement))
            {
                return new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Success,
                    Value = (T)(object)_settings
                };
            }

            return new StorageReadResult<T> { Status = StorageReadStatus.IoFailure };
        }

        public Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
            => Task.FromResult(new StorageWriteResult { Status = StorageWriteStatus.IoFailure });
    }

    private static void WriteMarkedLines(
        FileApplicationLogger logger,
        string prefix,
        int count,
        ConcurrentBag<string> markers)
    {
        for (var i = 0; i < count; i++)
        {
            var marker = $"{prefix}-{i:000000}";
            markers.Add(marker);
            logger.Log(ApplicationLogLevel.Information, "E", $"payload-{marker}-#SENTINEL");
        }
    }

    private static IReadOnlyList<string> ReadAllLogLines(string directory)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }
}
