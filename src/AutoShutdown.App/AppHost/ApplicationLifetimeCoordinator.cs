using System.Diagnostics;
using System.Windows;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Notifications;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.App.AppHost;

public sealed class ApplicationLifetimeCoordinator : IDisposable
{
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly IWindowActivationService _windowActivation;
    private readonly TrayIconService _trayIcon;
    private readonly ActivationPipeServer _pipeServer;
    private readonly ISchedulerEngine _schedulerEngine;
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
        ArgumentNullException.ThrowIfNull(dashboardRefreshService);
        ArgumentNullException.ThrowIfNull(notificationCoordinator);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(logRetention);

        _singleInstance = singleInstance;
        _windowActivation = windowActivation;
        _trayIcon = trayIcon;
        _pipeServer = pipeServer;
        _schedulerEngine = schedulerEngine;
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
