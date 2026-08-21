using System.Diagnostics;
using System.Windows;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Remote;
using AutoShutdown.App.Notifications;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Recovery;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.Storage;

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
    private readonly RecoveryNoticeService _recoveryNotice;
    private readonly TaskSyncCoordinator _taskSyncCoordinator;
    private readonly TaskSyncSettingsStore _taskSyncSettingsStore;
    private readonly RemoteServer _remoteServer;
    private readonly IApplicationLogger _logger;
    private readonly LogRetentionService _logRetention;
    private readonly CancellationTokenSource _appCts = new();
    private readonly object _exitGate = new();

    private Task? _engineTask;
    private Task? _exitTask;
    private bool _exiting;
    private bool _disposed;

    /// <summary>
    /// 单个启动步骤的统一超时（S-STARTUP-D1）：超过即视为启动卡死，fail-closed 失败处理，
    /// 绝不在 UI 线程无限等待任何可能阻塞的初始化。
    /// </summary>
    private static readonly TimeSpan StartupStepTimeout = TimeSpan.FromSeconds(20);

    public ApplicationLifetimeCoordinator(
        SingleInstanceCoordinator singleInstance,
        IWindowActivationService windowActivation,
        TrayIconService trayIcon,
        ActivationPipeServer pipeServer,
        ISchedulerEngine schedulerEngine,
        CrashRecoveryManager crashRecoveryManager,
        DashboardRefreshService dashboardRefreshService,
        NotificationCoordinator notificationCoordinator,
        RecoveryNoticeService recoveryNotice,
        TaskSyncCoordinator taskSyncCoordinator,
        TaskSyncSettingsStore taskSyncSettingsStore,
        RemoteServer remoteServer,
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
        ArgumentNullException.ThrowIfNull(recoveryNotice);
        ArgumentNullException.ThrowIfNull(taskSyncCoordinator);
        ArgumentNullException.ThrowIfNull(taskSyncSettingsStore);
        ArgumentNullException.ThrowIfNull(remoteServer);
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
        _recoveryNotice = recoveryNotice;
        // 在调度引擎启动（RunAsync 加载任务）前解析，保证 ctor 中订阅本地事实源事件不遗漏
        // 初始加载；Dispose 时解除订阅并取消在途防抖。
        _taskSyncCoordinator = taskSyncCoordinator;
        _taskSyncSettingsStore = taskSyncSettingsStore;
        _remoteServer = remoteServer;
        _logger = logger;
        _logRetention = logRetention;
        // S23 CP5：远程活动 → 托盘气泡（连接低危 / 触发·取消高危）。订阅在启动前建立，
        // 通知经 ShowBalloon 内部 Dispatcher 回 UI 线程；敏感材料绝不进提示。
        _remoteServer.Notification += OnRemoteNotification;
    }

    private void OnRemoteNotification(object? sender, RemoteServerNotification notification)
    {
        var (title, message, icon) = notification.Kind switch
        {
            RemoteServerNotificationKind.TriggerShutdown => (
                "高危：远程触发关机",
                "来自 " + notification.SourceIp + " 的远程设备已成功请求触发关机。",
                System.Windows.Forms.ToolTipIcon.Warning),
            RemoteServerNotificationKind.CancelShutdown => (
                "高危：远程取消关机",
                "来自 " + notification.SourceIp + " 的远程设备已成功请求取消待决关机。",
                System.Windows.Forms.ToolTipIcon.Warning),
            _ => (
                "远程控制活动",
                "来自 " + notification.SourceIp + " 的远程设备发起了一次连接。",
                System.Windows.Forms.ToolTipIcon.Info)
        };

        _logger.Info(
            "RemoteNotification",
            "远程活动提示（" + notification.Kind + "）：来源 " + notification.SourceIp + "。");
        _trayIcon.ShowBalloon(title, message, icon);
    }

    public void Start()
    {
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // S-STARTUP-D1：激活管道最先启动——即使后续启动步骤偏慢，次实例的激活/触发请求也能
        // 立刻到达并排队到 UI 线程处理，杜绝「主实例卡住但管道未起 → 次实例转发失败」。
        _logger.Info("ActivationPipeStarting", "激活管道启动中。");
        _pipeServer.Start();
        _logger.Info("ActivationPipeStarted", "激活管道已启动。");

        // 日志保留清理：线程池执行 + 限时，绝不阻塞 UI 线程启动；失败仅记日志。
        RunLogRetention();

        // 任务计划程序同步开关：线程池读取 task-sync.json（有界 + fail-closed），必须在调度
        // 引擎加载任务前决定（协调器 ctor 已在引擎启动前解析并订阅事实源事件）。
        LoadTaskSyncSettings();

        _logger.Info("SchedulerStarting", "调度引擎启动中。");
        RecoverFromCrash();
        _engineTask = _schedulerEngine.RunAsync(_appCts.Token);
        _logger.Info("SchedulerRunning", "调度引擎已启动。");
        _ = ObserveEngineAsync(_engineTask);

        // S23：调度引擎就绪后再按 remote-settings.json 启动远程控制（默认关闭，绝不静默启用）。
        // 线程池执行 + 限时：失败/超时保持不监听（fail-closed），且绝不让 UI 线程卡死在启动。
        StartRemoteServer();

        _trayIcon.ExitRequested = RequestExit;
        _trayIcon.Start();
        _dashboardRefreshService.Start();
        _notificationCoordinator.Start();

        _logger.Info("MainWindowActivating", "主窗口激活中。");
        _windowActivation.ActivateMainWindow();
        _logger.Info("MainWindowActivated", "主窗口已激活。");
        _logger.Info("ApplicationStarted", "应用启动完成。");
    }

    /// <summary>
    /// 日志保留清理：可能枚举/删除日志目录（磁盘停滞时可能长时间阻塞），故在线程池执行并限时；
    /// 失败只记日志，绝不阻塞启动。
    /// </summary>
    private void RunLogRetention()
    {
        try
        {
            Task.Run(() => _logRetention.RunOnce())
                .WaitAsync(StartupStepTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            _logger.Warning("LogRetentionFailed", "日志保留清理超时或失败（" + exception.Message + "），忽略。");
        }
    }

    /// <summary>
    /// 任务计划程序同步开关：有界、可记录、fail-closed。读取失败/超时一律保持关闭，
    /// 绝不静默启用（启用会创建外部计划任务）。
    /// </summary>
    private void LoadTaskSyncSettings()
    {
        var enabled = StartupTaskSyncSettingsGate.LoadEnabled(
            _taskSyncSettingsStore,
            _logger,
            StartupStepTimeout);
        _taskSyncCoordinator.Enabled = enabled;
    }

    /// <summary>
    /// 远程控制启动（S23）：线程池执行 + 限时。失败/超时保持不监听（fail-closed），
    /// 且绝不让 UI 线程在启动阶段无限等待（远程默认关闭，不属关键初始化）。
    /// </summary>
    private void StartRemoteServer()
    {
        try
        {
            Task.Run(() => _remoteServer.StartAsync(_appCts.Token))
                .WaitAsync(StartupStepTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            _logger.Warning("RemoteStartFailed", "远程控制启动失败或超时（" + exception.Message + "），保持不监听。");
        }
    }

    /// <summary>
    /// 调度循环前执行崩溃恢复。启动期间同步阻塞：恢复必须完成后再启动调度，
    /// 否则瞬态实例可能被调度循环当作在途任务继续执行。
    /// S-STARTUP-D1：后台线程执行 + WaitAsync 限时。超时是「状态未知」的关键初始化失败——
    /// 中止启动（异常向上传播，App 捕获后干净释放并非零退出），绝不在 UI 线程无限等待。
    /// </summary>
    private void RecoverFromCrash()
    {
        try
        {
            // 后台线程执行恢复：若在 UI 线程上 GetResult()，任何未使用
            // ConfigureAwait(false) 的存储 await 都会把 continuation 投递回已阻塞的
            // 调度器而永久挂起。线程池线程无 SynchronizationContext，杜绝该类死锁，
            // 同时保留「恢复完成后再启动调度」的同步顺序保证。
            var result = Task.Run(() => _crashRecoveryManager.RecoverAsync(_appCts.Token))
                .WaitAsync(StartupStepTimeout)
                .GetAwaiter().GetResult();

            // 恢复通知交给 UI 检查点（T08）呈现横幅；只读，不持久化。
            _recoveryNotice.Notice = result.InterruptedTaskIds.Count > 0
                ? new RecoveryNotice(result.InterruptedTaskIds)
                : null;

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
        catch (TimeoutException exception)
        {
            // 关键初始化超时：运行状态未知 → fail-closed，中止启动（不启动调度、不执行任何
            // 电源；App 捕获后释放全部资源与单实例所有权并无窗口非零退出）。
            _logger.Error(
                "CrashRecoveryTimeout",
                "崩溃恢复超过 " + (int)StartupStepTimeout.TotalSeconds
                + " 秒未完成；中止启动，释放单实例所有权并无窗口退出。",
                exception);
            throw;
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
        _remoteServer.Dispose();
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
        _taskSyncCoordinator.Dispose();
        _remoteServer.Notification -= OnRemoteNotification;
        _remoteServer.Dispose();
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
