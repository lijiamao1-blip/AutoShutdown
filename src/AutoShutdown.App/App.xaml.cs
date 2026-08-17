using System.Diagnostics;
using System.IO;
using System.Windows;
using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Recovery;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using Microsoft.Extensions.DependencyInjection;

namespace AutoShutdown.App;

public partial class App : System.Windows.Application
{
    private IServiceProvider? _serviceProvider;
    private ApplicationLifetimeCoordinator? _coordinator;
    private IApplicationLogger? _bootstrapLogger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // S-PKG：数据根目录解析（默认 %LocalAppData%\AutoShutdown；AUTOSHUTDOWN_DATA_ROOT
        // 环境变量可指向隔离沙箱，供干净机安装/升级/回滚/卸载与 GUI 冒烟使用）。
        var dataRoot = DataRootResolver.Resolve();
        var logger = new FileApplicationLogger(Path.Combine(dataRoot, "logs"));
        _bootstrapLogger = logger;
        logger.Info("ApplicationStarting", "应用启动。");
        logger.Info("DataRoot", "数据根目录：" + dataRoot);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogUnhandled(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogUnhandled(args.Exception);
            // Do not swallow the exception; keep the default crash behavior.
        };

        // S22 CP4：外部任务动作只回调本地应用（--trigger-task <id>）；触发 id 由本地唯一
        // 调度/Workflow 裁决执行电源，本应用绝不直接执行外部命令。
        var triggerTaskId = TryParseTriggerTaskId(e.Args);

        var singleInstance = new SingleInstanceCoordinator();
        var acquire = singleInstance.TryAcquirePrimary();
        if (acquire.Result != SingleInstanceResult.Primary)
        {
            if (acquire.Result == SingleInstanceResult.Secondary)
            {
                logger.Warning(
                    "SecondaryInstanceDetected",
                    triggerTaskId is not null
                        ? "检测到主实例已在运行，转发外部触发后退出。"
                        : "检测到主实例已在运行，发送激活通知后退出。");

                try
                {
                    var forwarded = triggerTaskId is { } forwardedId
                        ? ActivationPipeClient.TryTriggerAsync(forwardedId, CancellationToken.None)
                            .GetAwaiter().GetResult()
                        : ActivationPipeClient.TryActivateAsync(CancellationToken.None)
                            .GetAwaiter().GetResult();
                    if (!forwarded)
                    {
                        logger.Warning("ActivationForwardFailed", "主实例转发未确认。");
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine("Activation failed: " + exception.Message);
                }
            }
            else
            {
                logger.Warning(
                    "ApplicationStartupFailed",
                    "单实例互斥获取失败：" + acquire.Message);
            }

            singleInstance.Dispose();
            Shutdown();
            return;
        }

        logger.Info("PrimaryInstanceAcquired", "本实例成为主实例。");

        var services = new ServiceCollection();
        services.AddAutoShutdownServices(dataRoot);
        services.AddSingleton(singleInstance);
        // Override the default logger registration so the bootstrap instance
        // (which already wrote startup events) remains the single sink.
        services.AddSingleton<IApplicationLogger>(logger);
        _serviceProvider = services.BuildServiceProvider();

        if (triggerTaskId is { } primaryTriggerId)
        {
            // 外部触发回调的无界面模式：不打开主窗口，仅把触发交回本地 Workflow 后退出。
            HandleExternalTriggerAndExit(primaryTriggerId, logger);
            return;
        }

        _coordinator = _serviceProvider.GetRequiredService<ApplicationLifetimeCoordinator>();
        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bootstrapLogger?.Info("ApplicationStopped", "应用已退出。");

        try
        {
            _coordinator?.Dispose();
            // ActivationPipeServer 只实现 IAsyncDisposable：同步 Dispose() 会在管道服务器上
            // 抛 InvalidOperationException 并中断后续单例清理（含 S22 TaskSyncCoordinator）。
            // 显式走异步释放，保证全部单例按逆解析顺序完整 Dispose。
            if (_serviceProvider is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                (_serviceProvider as IDisposable)?.Dispose();
            }
        }
        catch
        {
            // Best-effort cleanup must never throw an unhandled exception.
        }

        _bootstrapLogger?.Dispose();

        base.OnExit(e);
    }

    private void LogUnhandled(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        _bootstrapLogger?.Error("UnhandledException", "捕获到未处理异常", exception);
    }

    /// <summary>
    /// 无界面外部触发处理（S22 CP4 + D2）：headless 启动下引擎尚未运行，此处先执行崩溃恢复
    /// 并启动调度引擎，再交付触发——触发必须经本地调度引擎唯一接入/仲裁路径执行（并发去重 +
    /// S20-D1/S20-D2 倒计时边界裁决 + 唯一 handler），服务 fail-closed 不直接执行电源；完成后
    /// 停止引擎并把 outcome 记入日志后退出。
    /// </summary>
    private void HandleExternalTriggerAndExit(Guid taskId, IApplicationLogger logger)
    {
        try
        {
            var engine = _serviceProvider!.GetRequiredService<ISchedulerEngine>();
            var crashRecovery = _serviceProvider!.GetRequiredService<CrashRecoveryManager>();
            var triggerService = _serviceProvider!.GetRequiredService<ExternalTaskTriggerService>();

            // 调度循环前执行崩溃恢复：中断未完成的瞬态实例，防止 headless 启动补执行电源。
            RunHeadlessCrashRecovery(crashRecovery, logger);

            using var engineCts = new CancellationTokenSource();
            var engineTask = engine.RunAsync(engineCts.Token);
            WaitUntilEngineRunning(engine);

            try
            {
                var outcome = triggerService.HandleExternalTriggerAsync(taskId, CancellationToken.None)
                    .GetAwaiter().GetResult();
                logger.Info(
                    "ExternalTriggerHandled",
                    "外部触发回调完成：" + outcome.Status
                    + (string.IsNullOrEmpty(outcome.Message) ? string.Empty : " — " + outcome.Message));
            }
            finally
            {
                engineCts.Cancel();
                try
                {
                    engineTask.Wait(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                    // 引擎停止是尽力而为；触发结果已交付。
                }
            }
        }
        catch (Exception exception)
        {
            logger.Error("ExternalTriggerFailed", "外部触发回调异常；不执行任何电源。", exception);
        }
        finally
        {
            try
            {
                // 同上：管道服务器只实现 IAsyncDisposable，须走异步释放以保证全部单例
                // 完整清理（外部触发 headless 路径同样持有完整 DI 图）。
                if (_serviceProvider is IAsyncDisposable asyncDisposable)
                {
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                else
                {
                    (_serviceProvider as IDisposable)?.Dispose();
                }
            }
            catch
            {
                // Best-effort cleanup must never throw during shutdown.
            }

            _bootstrapLogger?.Dispose();
            Shutdown();
        }
    }

    /// <summary>
    /// headless 崩溃恢复：与正常启动同序（调度循环前），把未完成的瞬态实例标记为 interrupted，
    /// 绝不补执行。后台线程执行避免 UI 线程死锁（存储 await 使用 ConfigureAwait(false)）。
    /// </summary>
    private static void RunHeadlessCrashRecovery(CrashRecoveryManager crashRecovery, IApplicationLogger logger)
    {
        try
        {
            var result = Task.Run(() => crashRecovery.RecoverAsync(CancellationToken.None))
                .GetAwaiter().GetResult();

            foreach (var taskId in result.InterruptedTaskIds)
            {
                logger.Warning(
                    "CrashRecovered",
                    "崩溃恢复：任务 " + taskId + " 已标记为中断（interrupted），不补执行。");
            }

            if (result.Status is CrashRecoveryStatus.Recovered
                or CrashRecoveryStatus.NoRecoveryNeeded
                or CrashRecoveryStatus.NotFound)
            {
                return;
            }

            logger.Error(
                "CrashRecoveryFailed",
                "崩溃恢复失败：" + string.Join(" ", result.Errors));
        }
        catch (Exception exception)
        {
            logger.Error("CrashRecoveryFailed", "崩溃恢复异常：" + exception.Message, exception);
        }
    }

    /// <summary>
    /// 等待引擎进入 Running：外部触发命令需经引擎唯一命令通道处理，必须先等调度循环就绪
    /// （RunAsync 恢复完成后才置 Running）。超时视为失败（fail-closed，不执行电源）。
    /// </summary>
    private static void WaitUntilEngineRunning(ISchedulerEngine engine)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            if (engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running)
            {
                return;
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException("The scheduler engine did not become ready in time; no power action.");
    }

    /// <summary>解析命令行中的 --trigger-task &lt;稳定本地 task id&gt;；无则返回 null。</summary>
    private static Guid? TryParseTriggerTaskId(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--trigger-task", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(args[i + 1], out var id)
                && id != Guid.Empty)
            {
                return id;
            }
        }

        return null;
    }
}
