using System.IO;
using System.Windows;
using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
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

        var logger = new FileApplicationLogger(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoShutdown",
            "logs"));
        _bootstrapLogger = logger;
        logger.Info("ApplicationStarting", "应用启动。");

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
        services.AddAutoShutdownServices();
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
    /// 无界面外部触发处理（S22 CP4）：只把触发交回本地唯一调度/Workflow 裁决。若闸门拒绝或
    /// 本地任务缺失/禁用，服务 fail-closed 不执行电源；此处只负责把 outcome 记入日志并退出。
    /// </summary>
    private void HandleExternalTriggerAndExit(Guid taskId, IApplicationLogger logger)
    {
        try
        {
            var triggerService = _serviceProvider!.GetRequiredService<ExternalTaskTriggerService>();
            var outcome = triggerService.HandleExternalTriggerAsync(taskId, CancellationToken.None)
                .GetAwaiter().GetResult();
            logger.Info(
                "ExternalTriggerHandled",
                "外部触发回调完成：" + outcome.Status
                + (string.IsNullOrEmpty(outcome.Message) ? string.Empty : " — " + outcome.Message));
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
