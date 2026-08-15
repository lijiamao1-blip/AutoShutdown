using System.Diagnostics;
using System.Windows;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Notifications;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Recovery;

namespace AutoShutdown.App.AppHost;

public sealed class ApplicationLifetimeCoordinator : IDisposable
{
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly IWindowActivationService _windowActivation;
    private readonly TrayIconService _trayIcon;
    private readonly ActivationPipeServer _pipeServer;
    private readonly ISchedulerEngine _schedulerEngine;
    private readonly CrashRecoveryManager _crashRecoveryManager;
    private readonly DashboardRefreshService _dashboardRefreshService;
    private readonly NotificationCoordinator _notificationCoordinator;
    private readonly IApplicationLogger _logger;
    private readonly LogRetentionService _logRetention;
    private readonly CancellationTokenSource _appCts = new();
    private readonly object _exitGate = new();

    private Task? _engineTask;
    private Task? _exitTask;
    private bool _exiting;
    private bool _disposed;

    public ApplicationLifetimeCoordinator(
        SingleInstanceCoordinator singleInstance,
        IWindowActivationService windowActivation,
        TrayIconService trayIcon,
        ActivationPipeServer pipeServer,
        ISchedulerEngine schedulerEngine,
        CrashRecoveryManager crashRecoveryManager,
        DashboardRefreshService dashboardRefreshService,
        NotificationCoordinator notificationCoordinator,
        IApplicationLogger logger,
        LogRetentionService logRetention)
    {
        ArgumentNullException.ThrowIfNull(singleInstance);
        ArgumentNullException.ThrowIfNull(windowActivation);
        ArgumentNullException.ThrowIfNull(trayIcon);
        ArgumentNullException.ThrowIfNull(pipeServer);
        ArgumentNullException.ThrowIfNull(schedulerEngine);
        ArgumentNullException.ThrowIfNull(crashRecoveryManager);
        ArgumentNullException.ThrowIfNull(dashboardRefreshService);
        ArgumentNullException.ThrowIfNull(notificationCoordinator);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(logRetention);

        _singleInstance = singleInstance;
        _windowActivation = windowActivation;
        _trayIcon = trayIcon;
        _pipeServer = pipeServer;
        _schedulerEngine = schedulerEngine;
        _crashRecoveryManager = crashRecoveryManager;
        _dashboardRefreshService = dashboardRefreshService;
        _notificationCoordinator = notificationCoordinator;
        _logger = logger;
        _logRetention = logRetention;
    }

    public void Start()
    {
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _logRetention.RunOnce();
        _logger.Info("SchedulerStarting", "调度引擎启动中。");
        RecoverFromCrash();
        _pipeServer.Start();
        _engineTask = _schedulerEngine.RunAsync(_appCts.Token);
        _logger.Info("SchedulerRunning", "调度引擎已启动。");
        _ = ObserveEngineAsync(_engineTask);
        _trayIcon.ExitRequested = RequestExit;
        _trayIcon.Start();
        _dashboardRefreshService.Start();
        _notificationCoordinator.Start();
        _windowActivation.ActivateMainWindow();
        _logger.Info("ApplicationStarted", "应用启动完成。");
    }

    /// <summary>
    /// 调度循环前执行崩溃恢复。启动期间同步阻塞：恢复必须完成后再启动调度，
    /// 否则瞬态实例可能被调度循环当作在途任务继续执行。
    /// </summary>
    private void RecoverFromCrash()
    {
        try
        {
            var result = _crashRecoveryManager.RecoverAsync(_appCts.Token).GetAwaiter().GetResult();

            foreach (var taskId in result.InterruptedTaskIds)
            {
                _logger.Warning(
                    "CrashRecovered",
                    "崩溃恢复：任务 " + taskId + " 已标记为中断（interrupted），不补执行。");
            }

            if (result.Status is CrashRecoveryStatus.Recovered
                or CrashRecoveryStatus.NoRecoveryNeeded
                or CrashRecoveryStatus.NotFound)
            {
                return;
            }

            _logger.Error(
                "CrashRecoveryFailed",
                "崩溃恢复失败：" + string.Join(" ", result.Errors));
        }
        catch (Exception exception)
        {
            _logger.Error("CrashRecoveryFailed", "崩溃恢复异常：" + exception.Message, exception);
        }
    }

    public void RequestExit()
    {
        if (!TryBeginExit())
        {
            return;
        }

        _logger.Info("ApplicationStopping", "应用正在退出。");
        _logger.Info("SchedulerStopping", "调度引擎正在停止。");
        _windowActivation.MarkExiting();
        _trayIcon.Dispose();
        _appCts.Cancel();
        _exitTask = CompleteExitAsync();
    }

    public void Dispose()
    {
        lock (_exitGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (!_exiting)
        {
            _windowActivation.MarkExiting();
            _trayIcon.Dispose();
            _appCts.Cancel();
        }

        _appCts.Dispose();
        _dashboardRefreshService.Dispose();
        _notificationCoordinator.Dispose();
        _singleInstance.Dispose();
    }

    private async Task CompleteExitAsync()
    {
        try
        {
            await _pipeServer.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine("Activation pipe shutdown failed: " + exception.Message);
        }

        try
        {
            if (_engineTask is not null)
            {
                await _engineTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine("Scheduler shutdown failed: " + exception.Message);
        }

        await ShutdownApplicationAsync().ConfigureAwait(false);
    }

    private bool TryBeginExit()
    {
        lock (_exitGate)
        {
            if (_exiting)
            {
                return false;
            }

            _exiting = true;
            return true;
        }
    }

    private async Task ObserveEngineAsync(Task engineTask)
    {
        try
        {
            await engineTask.ConfigureAwait(false);
            _logger.Info("SchedulerStopped", "调度引擎已停止。");
        }
        catch (Exception exception)
        {
            _logger.Error("SchedulerFaulted", "调度引擎故障：" + exception.Message);
        }
    }

    private static async Task ShutdownApplicationAsync()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        await application.Dispatcher.InvokeAsync(application.Shutdown);
    }
}
