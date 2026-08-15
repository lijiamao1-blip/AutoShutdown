using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.App.Presentation;

public enum TimeMode
{
    Countdown = 0,
    TodayAt = 1,
    DailyAt = 2
}

public sealed record NavItem(string Title, string Icon, string PageKey, bool IsPlaceholder);

public sealed record RecentActivityItem(string Time, string Text);

/// <summary>任务列表行（T08 UI 切片；由 SchedulerSnapshot.Instances 驱动）。</summary>
public sealed record TaskListItem(
    Guid TaskId,
    Guid InstanceId,
    Guid StageToken,
    string ActionText,
    string StateText,
    string FireTimeText,
    string CountdownText,
    string WarningText,
    TaskInstanceState State,
    bool CanStop,
    bool CanSnooze,
    bool CanClear);

/// <summary>强制冲突待决仲裁的候选任务（供 UI 询问用户）。</summary>
public sealed record PendingArbitrationItem(Guid TaskId, string ActionText, string FireTimeText);

public sealed class MainWindowViewModel : ObservableObject
{
    private const int MaxRecentActivities = 20;

    private readonly ISchedulerEngine _engine;
    private readonly IConfigurationService _configurationService;
    private readonly IClock _clock;
    private readonly IApplicationLogger _logger;
    private readonly IAutoStartService _autoStart;
    private readonly Func<bool>? _autoStartConfirmation;
    private readonly Func<bool>? _cancelConfirmation;
    private readonly Func<bool>? _realPowerConfirmation;
    private readonly RecoveryNoticeService? _recoveryNoticeService;

    private TaskInstance? _currentInstance;
    private TaskInstanceState _lastState = TaskInstanceState.Unknown;
    private Guid _lastInstanceId;
    private Guid _lastToken;
    private SchedulerEngineStatus _engineStatus = SchedulerEngineStatus.Unknown;
    private bool _configUsable;
    private bool _configRealPowerEnabled;
    private string _configStatusText = "未知状态";
    private string _headerModeText = "安全测试模式";
    private string _headerModeHint = "当前不会执行真实系统电源操作";
    private bool _isSubmitting;
    private bool _isConfigInitializing;

    public MainWindowViewModel(
        ISchedulerEngine engine,
        IConfigurationService configurationService,
        IClock clock,
        IApplicationLogger logger,
        IAutoStartService autoStart,
        Func<bool>? autoStartConfirmation = null,
        Func<bool>? cancelConfirmation = null,
        Func<bool>? realPowerConfirmation = null,
        RecoveryNoticeService? recoveryNotice = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(autoStart);

        _engine = engine;
        _configurationService = configurationService;
        _clock = clock;
        _logger = logger;
        _autoStart = autoStart;
        _autoStartConfirmation = autoStartConfirmation;
        _cancelConfirmation = cancelConfirmation;
        _realPowerConfirmation = realPowerConfirmation;
        _recoveryNoticeService = recoveryNotice;

        NavItems =
        [
            new NavItem("首页", "⌂", "home", false),
            new NavItem("任务管理", "▤", "tasks", false),
            new NavItem("高级功能", "◈", "advanced", true),
            new NavItem("网络唤醒", "⇪", "wol", true),
            new NavItem("日志与诊断", "▤", "logs", true),
            new NavItem("软件设置", "⚙", "settings", false),
            new NavItem("关于软件", "ℹ", "about", true)
        ];
        _selectedNav = NavItems[0];

        CreateCommand = new AsyncRelayCommand(ExecuteCreateAsync, () => CanCreateNow(out _));
        SnoozeCommand = new AsyncRelayCommand(ExecuteSnoozeAsync, () => CanSnooze);
        CancelCommand = new AsyncRelayCommand(ExecuteCancelAsync, () => CanCancel);
        ClearCommand = new AsyncRelayCommand(ExecuteClearAsync, () => CanClear);
        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is NavItem item)
            {
                SelectedNav = item;
            }
        });
        SettingsCommand = new RelayCommand(() => SelectedNav = NavItems[5]);
        TrayMenuCommand = new RelayCommand(() =>
            System.Windows.MessageBox.Show(
                "托盘菜单位于系统通知区域（靠近时钟）。\n双击托盘图标可重新打开控制面板；右键菜单可打开控制面板或退出程序。",
                "托盘菜单",
                MessageBoxButton.OK,
                MessageBoxImage.Information));
        ToggleNavCommand = new RelayCommand(() => IsNavCollapsed = !IsNavCollapsed);
        HomeCommand = new RelayCommand(() => SelectedNav = NavItems[0]);
        CopyLogPathCommand = new RelayCommand(CopyLogPath);
        InitializeConfigCommand = new AsyncRelayCommand(
            ExecuteInitializeConfigAsync,
            () => !_isConfigInitializing);
        EnableAutoStartCommand = new AsyncRelayCommand(ExecuteEnableAutoStartAsync, () => !IsAutoStartBusy);
        DisableAutoStartCommand = new AsyncRelayCommand(ExecuteDisableAutoStartAsync, () => !IsAutoStartBusy);
        RepairAutoStartCommand = new AsyncRelayCommand(ExecuteRepairAutoStartAsync, () => !IsAutoStartBusy);
        RowSnoozeCommand = new RelayCommand(RowSnooze);
        RowStopCommand = new RelayCommand(RowStop);
        RowClearCommand = new RelayCommand(RowClear);
        ResolveArbitrationCommand = new RelayCommand(ResolveArbitration);
    }

    // ---- 导航 ----

    public IReadOnlyList<NavItem> NavItems { get; }

    private NavItem _selectedNav;

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetProperty(ref _selectedNav, value))
            {
                OnPropertyChanged(nameof(IsHomeVisible));
                OnPropertyChanged(nameof(IsPlaceholderVisible));
                OnPropertyChanged(nameof(IsPlaceholderAreaVisible));
                OnPropertyChanged(nameof(PlaceholderTitle));
                OnPropertyChanged(nameof(IsTaskSummaryVisible));
                OnPropertyChanged(nameof(IsLogsPageVisible));
                OnPropertyChanged(nameof(IsSettingsPageVisible));
                OnPropertyChanged(nameof(IsTasksPageVisible));
                OnPropertyChanged(nameof(IsGenericPlaceholderVisible));
            }
        }
    }

    private bool _isNavCollapsed;

    public bool IsNavCollapsed
    {
        get => _isNavCollapsed;
        set
        {
            if (SetProperty(ref _isNavCollapsed, value))
            {
                OnPropertyChanged(nameof(NavColumnWidth));
            }
        }
    }

    public double NavColumnWidth => IsNavCollapsed ? 64 : 220;

    public bool IsHomeVisible => SelectedNav.PageKey == "home";

    public bool IsPlaceholderVisible => !IsHomeVisible;

    public string PlaceholderTitle => SelectedNav.Title;

    public bool IsTaskSummaryVisible => SelectedNav.PageKey == "tasks";

    public bool IsLogsPageVisible => SelectedNav.PageKey == "logs";

    public bool IsSettingsPageVisible => SelectedNav.PageKey == "settings";

    public bool IsTasksPageVisible => SelectedNav.PageKey == "tasks";

    /// <summary>占位区（日志页/设置页/普通占位页）；任务管理页拥有真实页面，不进入占位区。</summary>
    public bool IsPlaceholderAreaVisible => IsPlaceholderVisible && !IsTasksPageVisible;

    /// <summary>普通占位页：非首页、非日志页、非设置页、非任务管理页时才显示。</summary>
    public bool IsGenericPlaceholderVisible
        => IsPlaceholderVisible
            && !IsLogsPageVisible
            && !IsSettingsPageVisible
            && !IsTasksPageVisible;

    public string LogDirectory => _logger.LogDirectory;

    public ICommand CopyLogPathCommand { get; }

    private string _taskSummaryText = string.Empty;

    public string TaskSummaryText
    {
        get => _taskSummaryText;
        private set => SetProperty(ref _taskSummaryText, value);
    }

    // ---- 创建表单 ----

    private TimeMode _selectedMode = TimeMode.Countdown;

    public TimeMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(ModeIsCountdown));
                OnPropertyChanged(nameof(ModeIsTodayAt));
                OnPropertyChanged(nameof(ModeIsDailyAt));
                RefreshCreateState();
            }
        }
    }

    public bool ModeIsCountdown
    {
        get => SelectedMode == TimeMode.Countdown;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.Countdown;
            }
        }
    }

    public bool ModeIsTodayAt
    {
        get => SelectedMode == TimeMode.TodayAt;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.TodayAt;
            }
        }
    }

    public bool ModeIsDailyAt
    {
        get => SelectedMode == TimeMode.DailyAt;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.DailyAt;
            }
        }
    }

    public IReadOnlyList<string> CountdownHourOptions { get; } =
        Enumerable.Range(0, 169).Select(value => $"{value:00} 小时").ToArray();

    public IReadOnlyList<string> ClockHourOptions { get; } =
        Enumerable.Range(0, 24).Select(value => $"{value:00} 小时").ToArray();

    public IReadOnlyList<string> MinuteOptions { get; } =
        Enumerable.Range(0, 60).Select(value => $"{value:00} 分钟").ToArray();

    public IReadOnlyList<string> SecondOptions { get; } =
        Enumerable.Range(0, 60).Select(value => $"{value:00} 秒").ToArray();

    private string _countdownHoursText = "00 小时";

    public string CountdownHoursText
    {
        get => _countdownHoursText;
        set
        {
            if (SetProperty(ref _countdownHoursText, value))
            {
                RefreshCreateState();
            }
        }
    }

    private string _countdownMinutesText = "30 分钟";

    public string CountdownMinutesText
    {
        get => _countdownMinutesText;
        set
        {
            if (SetProperty(ref _countdownMinutesText, value))
            {
                RefreshCreateState();
            }
        }
    }

    private string _countdownSecondsText = "00 秒";

    public string CountdownSecondsText
    {
        get => _countdownSecondsText;
        set
        {
            if (SetProperty(ref _countdownSecondsText, value))
            {
                RefreshCreateState();
            }
        }
    }

    private string _timeHoursText = "00 小时";

    public string TimeHoursText
    {
        get => _timeHoursText;
        set
        {
            if (SetProperty(ref _timeHoursText, value))
            {
                RefreshCreateState();
            }
        }
    }

    private string _timeMinutesText = "00 分钟";

    public string TimeMinutesText
    {
        get => _timeMinutesText;
        set
        {
            if (SetProperty(ref _timeMinutesText, value))
            {
                RefreshCreateState();
            }
        }
    }

    private string _timeSecondsText = "00 秒";

    public string TimeSecondsText
    {
        get => _timeSecondsText;
        set
        {
            if (SetProperty(ref _timeSecondsText, value))
            {
                RefreshCreateState();
            }
        }
    }

    private PowerAction _selectedAction = PowerAction.Shutdown;

    public PowerAction SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (SetProperty(ref _selectedAction, value))
            {
                OnPropertyChanged(nameof(ActionIsShutdown));
                OnPropertyChanged(nameof(ActionIsRestart));
                OnPropertyChanged(nameof(ActionIsSleep));
                OnPropertyChanged(nameof(ActionIsHibernate));
            }
        }
    }

    public bool ActionIsShutdown
    {
        get => SelectedAction == PowerAction.Shutdown;
        set
        {
            if (value)
            {
                SelectedAction = PowerAction.Shutdown;
            }
        }
    }

    public bool ActionIsRestart
    {
        get => SelectedAction == PowerAction.Restart;
        set
        {
            if (value)
            {
                SelectedAction = PowerAction.Restart;
            }
        }
    }

    public bool ActionIsSleep
    {
        get => SelectedAction == PowerAction.Sleep;
        set
        {
            if (value)
            {
                SelectedAction = PowerAction.Sleep;
            }
        }
    }

    public bool ActionIsHibernate
    {
        get => SelectedAction == PowerAction.Hibernate;
        set
        {
            if (value)
            {
                SelectedAction = PowerAction.Hibernate;
            }
        }
    }

    public IReadOnlyList<string> ReminderOptions { get; } = ["不提醒", "提前 1 分钟", "提前 5 分钟", "提前 10 分钟", "提前 30 分钟"];

    private int _reminderIndex = 3;

    public int ReminderIndex
    {
        get => _reminderIndex;
        set
        {
            if (SetProperty(ref _reminderIndex, value))
            {
                RefreshCreateState();
            }
        }
    }

    // ---- 当前任务 ----

    private string _currentStateText = "当前没有活动任务";

    public string CurrentStateText
    {
        get => _currentStateText;
        private set => SetProperty(ref _currentStateText, value);
    }

    private string _countdownText = "—";

    public string CountdownText
    {
        get => _countdownText;
        private set => SetProperty(ref _countdownText, value);
    }

    private string _nextFireTimeText = "--";

    public string NextFireTimeText
    {
        get => _nextFireTimeText;
        private set => SetProperty(ref _nextFireTimeText, value);
    }

    private string _taskActionText = "--";

    public string TaskActionText
    {
        get => _taskActionText;
        private set => SetProperty(ref _taskActionText, value);
    }

    private string _warningTimeText = "--";

    public string WarningTimeText
    {
        get => _warningTimeText;
        private set => SetProperty(ref _warningTimeText, value);
    }

    private bool _hasCurrentTask;

    public bool HasCurrentTask
    {
        get => _hasCurrentTask;
        private set => SetProperty(ref _hasCurrentTask, value);
    }

    private bool _canSnooze;

    public bool CanSnooze
    {
        get => _canSnooze;
        private set => SetProperty(ref _canSnooze, value);
    }

    private string _snoozeDisabledReason = string.Empty;

    public string SnoozeDisabledReason
    {
        get => _snoozeDisabledReason;
        private set => SetProperty(ref _snoozeDisabledReason, value);
    }

    private bool _canCancel;

    public bool CanCancel
    {
        get => _canCancel;
        private set => SetProperty(ref _canCancel, value);
    }

    private string _cancelDisabledReason = string.Empty;

    public string CancelDisabledReason
    {
        get => _cancelDisabledReason;
        private set => SetProperty(ref _cancelDisabledReason, value);
    }

    private bool _canClear;

    public bool CanClear
    {
        get => _canClear;
        private set => SetProperty(ref _canClear, value);
    }

    // ---- 任务列表与仲裁（T08 UI 切片） ----

    public ObservableCollection<TaskListItem> TaskItems { get; } = [];

    public ObservableCollection<PendingArbitrationItem> PendingArbitrationItems { get; } = [];

    private bool _hasTasks;

    public bool HasTasks
    {
        get => _hasTasks;
        private set => SetProperty(ref _hasTasks, value);
    }

    private bool _hasNoTasks;

    public bool HasNoTasks
    {
        get => _hasNoTasks;
        private set => SetProperty(ref _hasNoTasks, value);
    }

    private bool _hasPendingArbitration;

    public bool HasPendingArbitration
    {
        get => _hasPendingArbitration;
        private set => SetProperty(ref _hasPendingArbitration, value);
    }

    private string _arbitrationDecisionText = string.Empty;

    public string ArbitrationDecisionText
    {
        get => _arbitrationDecisionText;
        private set
        {
            if (SetProperty(ref _arbitrationDecisionText, value))
            {
                OnPropertyChanged(nameof(HasArbitrationDecision));
            }
        }
    }

    public bool HasArbitrationDecision => !string.IsNullOrEmpty(_arbitrationDecisionText);

    private string _recoveryNoticeText = string.Empty;

    public string RecoveryNoticeText
    {
        get => _recoveryNoticeText;
        private set
        {
            if (SetProperty(ref _recoveryNoticeText, value))
            {
                OnPropertyChanged(nameof(HasRecoveryNotice));
            }
        }
    }

    public bool HasRecoveryNotice => !string.IsNullOrEmpty(_recoveryNoticeText);

    // ---- 状态卡 ----

    private string _schedulerStatusText = "未知状态";

    public string SchedulerStatusText
    {
        get => _schedulerStatusText;
        private set => SetProperty(ref _schedulerStatusText, value);
    }

    private string _schedulerFaultText = string.Empty;

    public string SchedulerFaultText
    {
        get => _schedulerFaultText;
        private set
        {
            if (SetProperty(ref _schedulerFaultText, value))
            {
                OnPropertyChanged(nameof(HasSchedulerFault));
            }
        }
    }

    public bool HasSchedulerFault => !string.IsNullOrEmpty(_schedulerFaultText);

    public string ConfigStatusText
    {
        get => _configStatusText;
        private set
        {
            if (SetProperty(ref _configStatusText, value))
            {
                OnPropertyChanged(nameof(IsConfigInitVisible));
            }
        }
    }

    /// <summary>仅当配置不可用时显示"初始化安全配置"入口。</summary>
    public bool IsConfigInitVisible => !_configUsable;

    /// <summary>顶部模式标签：安全测试模式 / 真实电源模式。</summary>
    public string HeaderModeText
    {
        get => _headerModeText;
        private set => SetProperty(ref _headerModeText, value);
    }

    /// <summary>顶部模式提示文字。</summary>
    public string HeaderModeHint
    {
        get => _headerModeHint;
        private set => SetProperty(ref _headerModeHint, value);
    }

    private string _configInitErrorText = string.Empty;

    public string ConfigInitErrorText
    {
        get => _configInitErrorText;
        private set
        {
            if (SetProperty(ref _configInitErrorText, value))
            {
                OnPropertyChanged(nameof(HasConfigInitError));
            }
        }
    }

    public bool HasConfigInitError => !string.IsNullOrEmpty(_configInitErrorText);

    // ---- 创建状态 ----

    private string _createDisabledReason = string.Empty;

    public string CreateDisabledReason
    {
        get => _createDisabledReason;
        private set => SetProperty(ref _createDisabledReason, value);
    }

    private string _statusMessage = string.Empty;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ---- 开机自启动 ----

    private AutoStartStatus _autoStartStatus = AutoStartStatus.Disabled;

    public AutoStartStatus AutoStartStatus
    {
        get => _autoStartStatus;
        private set
        {
            if (SetProperty(ref _autoStartStatus, value))
            {
                OnPropertyChanged(nameof(AutoStartStatusText));
                OnPropertyChanged(nameof(IsAutoStartEnabled));
                OnPropertyChanged(nameof(IsAutoStartRepairVisible));
            }
        }
    }

    public string AutoStartStatusText => MapAutoStartStatus(AutoStartStatus);

    public bool IsAutoStartEnabled => AutoStartStatus == AutoStartStatus.Enabled;

    public bool IsAutoStartRepairVisible
        => AutoStartStatus is AutoStartStatus.PathMismatch or AutoStartStatus.InvalidValue;

    private bool _isAutoStartBusy;

    public bool IsAutoStartBusy
    {
        get => _isAutoStartBusy;
        private set
        {
            if (SetProperty(ref _isAutoStartBusy, value))
            {
                EnableAutoStartCommand.RaiseCanExecuteChanged();
                DisableAutoStartCommand.RaiseCanExecuteChanged();
                RepairAutoStartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _autoStartErrorText = string.Empty;

    public string AutoStartErrorText
    {
        get => _autoStartErrorText;
        private set
        {
            if (SetProperty(ref _autoStartErrorText, value))
            {
                OnPropertyChanged(nameof(HasAutoStartError));
            }
        }
    }

    public bool HasAutoStartError => !string.IsNullOrEmpty(_autoStartErrorText);

    public AsyncRelayCommand EnableAutoStartCommand { get; }

    public AsyncRelayCommand DisableAutoStartCommand { get; }

    public AsyncRelayCommand RepairAutoStartCommand { get; }

    public void RefreshAutoStart()
    {
        AutoStartStatus status;
        try
        {
            status = _autoStart.GetStatus();
        }
        catch
        {
            status = AutoStartStatus.Unavailable;
        }

        AutoStartStatus = status;
        TryLog(logger => logger.Info(
            "AutoStartStatusChecked",
            "自启动状态：" + MapAutoStartStatus(status) + "。"));
    }

    private async Task ExecuteEnableAutoStartAsync()
    {
        if (IsAutoStartBusy)
        {
            return;
        }

        if (!ConfirmEnableAutoStart())
        {
            AppendActivity("已取消启用开机自启动");
            return;
        }

        await RunAutoStartOperationAsync(
            () => _autoStart.Enable(),
            "AutoStartEnableSucceeded",
            "AutoStartEnableFailed",
            "开机自启动已启用",
            "开机自启动启用失败");
    }

    private async Task ExecuteDisableAutoStartAsync()
    {
        if (IsAutoStartBusy)
        {
            return;
        }

        await RunAutoStartOperationAsync(
            () => _autoStart.Disable(),
            "AutoStartDisableSucceeded",
            "AutoStartDisableFailed",
            "开机自启动已关闭",
            "开机自启动关闭失败");
    }

    private async Task ExecuteRepairAutoStartAsync()
    {
        if (IsAutoStartBusy)
        {
            return;
        }

        await RunAutoStartOperationAsync(
            () => _autoStart.Repair(),
            "AutoStartRepairSucceeded",
            "AutoStartRepairFailed",
            "自启动注册项已修复",
            "自启动修复失败");
    }

    private Task RunAutoStartOperationAsync(
        Func<AutoStartOperationResult> operation,
        string successEvent,
        string failureEvent,
        string successActivity,
        string failureActivity)
    {
        IsAutoStartBusy = true;
        AutoStartErrorText = string.Empty;

        try
        {
            var result = operation();
            if (result.Succeeded)
            {
                TryLog(logger => logger.Info(successEvent, successActivity + "。"));
                StatusMessage = successActivity;
                AppendActivity(successActivity);
            }
            else
            {
                TryLog(logger => logger.Warning(failureEvent, failureActivity + "：" + result.ResultCode + "。"));
                AutoStartErrorText = result.Message;
                StatusMessage = failureActivity + "：" + result.Message;
                AppendActivity(failureActivity + "：" + result.Message);
            }
        }
        finally
        {
            // 操作完成后重新读取真实状态，不显示缓存结果。
            RefreshAutoStart();
            IsAutoStartBusy = false;
        }

        return Task.CompletedTask;
    }

    private bool ConfirmEnableAutoStart()
    {
        if (_autoStartConfirmation is not null)
        {
            return _autoStartConfirmation();
        }

        return System.Windows.MessageBox.Show(
            "启用后，开机时将自动运行本程序。\n仅为当前 Windows 用户启动，可随时在软件设置中关闭。",
            "启用开机自启动",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private static string MapAutoStartStatus(AutoStartStatus status) => status switch
    {
        AutoStartStatus.Disabled => "未启用",
        AutoStartStatus.Enabled => "已启用",
        AutoStartStatus.PathMismatch => "路径异常",
        AutoStartStatus.InvalidValue => "值异常",
        AutoStartStatus.Unavailable => "不可用",
        _ => "未知状态"
    };

    // ---- 最近活动 ----

    public ObservableCollection<RecentActivityItem> RecentActivities { get; } = [];

    // ---- 命令 ----

    public AsyncRelayCommand CreateCommand { get; }

    public AsyncRelayCommand SnoozeCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand ClearCommand { get; }

    public ICommand NavigateCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand TrayMenuCommand { get; }

    public ICommand ToggleNavCommand { get; }

    public ICommand HomeCommand { get; }

    public ICommand RowSnoozeCommand { get; }

    public ICommand RowStopCommand { get; }

    public ICommand RowClearCommand { get; }

    public ICommand ResolveArbitrationCommand { get; }

    public AsyncRelayCommand InitializeConfigCommand { get; }

    public async Task InitializeAsync()
    {
        Refresh(GetSnapshot(), _clock.UtcNow);
        await RefreshConfigurationAsync();
        RefreshAutoStart();
        RefreshRecoveryNotice();
    }

    public async Task RefreshConfigurationAsync()
    {
        try
        {
            var load = await _configurationService.LoadAsync(CancellationToken.None);
            if (load.Status == ConfigurationLoadStatus.Success && load.Config is not null && load.Config.SchemaVersion == 1)
            {
                var config = load.Config;
                // 配置可用条件：安全测试模式（TestMode=true），或显式开启真实电源模式
                // （TestMode=false 且 RealPowerEnabled=true）。其余（TestMode=false 且
                // RealPowerEnabled=false，或互斥校验失败）一律视为不可用。
                _configRealPowerEnabled = config.RealPowerEnabled && !config.TestMode;
                _configUsable = config.TestMode || _configRealPowerEnabled;
                ConfigStatusText = config.TestMode
                    ? "安全有效"
                    : (config.RealPowerEnabled ? "真实电源模式" : "测试模式已关闭（拒绝执行）");
                HeaderModeText = _configRealPowerEnabled ? "真实电源模式" : "安全测试模式";
                HeaderModeHint = _configRealPowerEnabled
                    ? "当前将执行真实系统电源操作，请谨慎使用"
                    : "当前不会执行真实系统电源操作";
                TryLog(logger => logger.Info(
                    "ConfigurationLoaded",
                    "配置加载成功，SchemaVersion=" + config.SchemaVersion
                        + "，TestMode=" + config.TestMode
                        + "，RealPowerEnabled=" + config.RealPowerEnabled + "。"));
            }
            else
            {
                _configUsable = false;
                ConfigStatusText = "配置不可用：" + UiTextMapper.MapConfig(load.Status);
                HeaderModeText = "配置不可用";
                HeaderModeHint = "配置加载失败，请检查设置";
                TryLog(logger => logger.Warning(
                    "ConfigurationUnavailable",
                    "配置不可用，状态：" + load.Status));
            }
        }
        catch (Exception exception)
        {
            _configUsable = false;
            ConfigStatusText = "配置不可用：" + exception.Message;
            HeaderModeText = "配置不可用";
            HeaderModeHint = "配置加载异常，请检查设置";
            TryLog(logger => logger.Warning("ConfigurationUnavailable", "配置加载异常。"));
        }

        RefreshCreateState();
    }

    /// <summary>
    /// 用户主动创建安全测试模式初始配置。走现有 ConfigurationService 校验与
    /// Storage 原子写入；失败时保持安全拒绝，不在内存中冒充加载成功。
    /// </summary>
    private async Task ExecuteInitializeConfigAsync()
    {
        if (_isConfigInitializing)
        {
            return;
        }

        _isConfigInitializing = true;
        InitializeConfigCommand.RaiseCanExecuteChanged();
        ConfigInitErrorText = string.Empty;

        try
        {
            var result = await _configurationService.CreateSafeDefaultAsync(CancellationToken.None);
            if (result.Status == ConfigurationSaveStatus.Success)
            {
                TryLog(logger => logger.Info("ConfigurationInitialized", "安全初始配置已创建。"));
                StatusMessage = "安全初始配置已创建";
                AppendActivity("已初始化安全配置");
            }
            else
            {
                var detail = string.Join("；", result.Errors);
                ConfigInitErrorText = "初始化失败：" + (string.IsNullOrEmpty(detail) ? "写入失败" : detail);
                TryLog(logger => logger.Warning("ConfigurationInitFailed", "安全配置初始化失败。"));
            }
        }
        catch (Exception exception)
        {
            ConfigInitErrorText = "初始化失败：" + exception.Message;
            TryLog(logger => logger.Warning("ConfigurationInitFailed", "安全配置初始化异常。"));
        }
        finally
        {
            _isConfigInitializing = false;
            InitializeConfigCommand.RaiseCanExecuteChanged();
            // 重新读取真实磁盘状态；初始化失败时保持"配置缺失默认拒绝"。
            await RefreshConfigurationAsync();
        }
    }

    public void Refresh(SchedulerSnapshot snapshot, DateTimeOffset now)
    {
        _engineStatus = snapshot.EngineStatus;
        SchedulerStatusText = UiTextMapper.MapEngine(snapshot.EngineStatus);
        SchedulerFaultText = snapshot.EngineStatus == SchedulerEngineStatus.Faulted
            ? (snapshot.FaultMessage ?? "调度服务发生故障")
            : string.Empty;

        // 任务列表与仲裁呈现独立于「主实例」选择，始终刷新。
        RefreshTaskList(snapshot, now);
        RefreshArbitration(snapshot);

        var instance = SelectPrimaryInstance(snapshot);
        if (instance is null)
        {
            if (_currentInstance is not null)
            {
                AppendActivity("任务已结束（无当前任务）");
            }

            _currentInstance = null;
            HasCurrentTask = false;
            CurrentStateText = "当前没有活动任务";
            CountdownText = "—";
            NextFireTimeText = "--";
            TaskActionText = "--";
            WarningTimeText = "--";
            TaskSummaryText = "当前没有活动任务";
            CanSnooze = false;
            CanCancel = false;
            CanClear = false;
            SnoozeDisabledReason = "当前没有活动任务";
            CancelDisabledReason = "当前没有活动任务";
            RefreshCreateState();
            RaiseAllCommands();
            return;
        }

        _currentInstance = instance;
        HasCurrentTask = true;
        CurrentStateText = UiTextMapper.Map(instance.State);
        TaskActionText = UiTextMapper.Map(instance.ActionSnapshot);
        var localFire = TimeZoneInfo.ConvertTime(instance.ScheduledFireTime, _clock.LocalTimeZone);
        var localWarning = instance.WarningStartTime is null
            ? (DateTimeOffset?)null
            : TimeZoneInfo.ConvertTime(instance.WarningStartTime.Value, _clock.LocalTimeZone);
        NextFireTimeText = localFire.ToString("yyyy-MM-dd HH:mm:ss");
        WarningTimeText = localWarning?.ToString("HH:mm:ss") ?? "无";
        TaskSummaryText = $"当前任务：{UiTextMapper.Map(instance.State)} / {UiTextMapper.Map(instance.ActionSnapshot)} / {localFire:yyyy-MM-dd HH:mm}";

        var remaining = instance.ScheduledFireTime - now;
        CountdownText = remaining > TimeSpan.Zero
            ? remaining.ToString(@"hh\:mm\:ss")
            : "即将执行";

        CanSnooze = instance.State is TaskInstanceState.Waiting or TaskInstanceState.Confirming
            && instance.InstanceId != Guid.Empty
            && instance.StageToken != Guid.Empty;
        SnoozeDisabledReason = CanSnooze
            ? string.Empty
            : (instance.InstanceId == Guid.Empty || instance.StageToken == Guid.Empty
                ? "任务标识或阶段令牌缺失"
                : "当前状态不允许延迟");
        CanCancel = instance.State is TaskInstanceState.Waiting or TaskInstanceState.Confirming;
        CancelDisabledReason = CanCancel ? string.Empty : "当前状态不允许取消";
        CanClear = instance.State is TaskInstanceState.Cancelled
            or TaskInstanceState.Executed
            or TaskInstanceState.Faulted
            or TaskInstanceState.Interrupted;

        if (instance.State != _lastState
            || instance.InstanceId != _lastInstanceId
            || instance.StageToken != _lastToken)
        {
            _lastState = instance.State;
            _lastInstanceId = instance.InstanceId;
            _lastToken = instance.StageToken;
            AppendActivity($"任务状态：{UiTextMapper.Map(instance.State)}");
        }

        RefreshCreateState();
        RaiseAllCommands();
    }

    public bool CanCreateNow(out string reason)
    {
        if (_engineStatus != SchedulerEngineStatus.Running)
        {
            reason = "调度服务未运行";
            return false;
        }

        if (!_configUsable)
        {
            reason = ConfigStatusText;
            return false;
        }

        if (_currentInstance is not null && !IsTerminal(_currentInstance.State))
        {
            reason = "已有活动任务";
            return false;
        }

        if (!TryBuildTimeInput(out _, out _, out var inputError))
        {
            reason = inputError;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryBuildTimeInput(out TimeSpan? duration, out TimeOnly? target, out string error)
    {
        duration = null;
        target = null;

        if (SelectedMode == TimeMode.Countdown)
        {
            if (!TryParseRange(CountdownHoursText, 0, 168, "小时", out var hours, out error))
            {
                return false;
            }

            if (!TryParseRange(CountdownMinutesText, 0, 59, "分钟", out var minutes, out error))
            {
                return false;
            }

            if (!TryParseRange(CountdownSecondsText, 0, 59, "秒", out var seconds, out error))
            {
                return false;
            }

            var total = (hours * 3600) + (minutes * 60) + seconds;
            if (total <= 0)
            {
                error = "总时长必须大于 0";
                return false;
            }

            duration = TimeSpan.FromSeconds(total);
            error = string.Empty;
            return true;
        }

        if (!TryParseRange(TimeHoursText, 0, 23, "小时", out var timeHours, out error))
        {
            return false;
        }

        if (!TryParseRange(TimeMinutesText, 0, 59, "分钟", out var timeMinutes, out error))
        {
            return false;
        }

        if (!TryParseRange(TimeSecondsText, 0, 59, "秒", out var timeSeconds, out error))
        {
            return false;
        }

        target = new TimeOnly(timeHours, timeMinutes, timeSeconds);
        error = string.Empty;
        return true;
    }

    public bool TryBuildDefinition(out TaskDefinition definition, out string error)
    {
        definition = null!;

        if (SelectedAction is not (PowerAction.Shutdown or PowerAction.Restart or PowerAction.Sleep or PowerAction.Hibernate))
        {
            error = "请选择有效的任务类型";
            return false;
        }

        if (!TryBuildTimeInput(out var duration, out var target, out error))
        {
            return false;
        }

        var kind = SelectedMode switch
        {
            TimeMode.Countdown => TaskKind.Countdown,
            TimeMode.TodayAt => TaskKind.TodayAt,
            _ => TaskKind.DailyAt
        };

        // 真实电源模式：创建真实任务前必须获得用户明确人工确认（双闸门之二）。
        var realPowerConfirmed = false;
        if (_configRealPowerEnabled)
        {
            var confirmed = _realPowerConfirmation is not null
                ? _realPowerConfirmation()
                : System.Windows.MessageBox.Show(
                    "当前为真实电源模式。任务到期后将执行真实关机/重启/睡眠/休眠，\n请先保存所有工作。确认创建？",
                    "真实电源确认",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK;
            if (!confirmed)
            {
                error = "已取消创建真实电源任务";
                return false;
            }

            realPowerConfirmed = true;
        }

        definition = new TaskDefinition
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Action = SelectedAction,
            CountdownDuration = kind == TaskKind.Countdown ? duration : null,
            TargetTimeOfDay = kind == TaskKind.Countdown ? null : target,
            WarningSeconds = GetWarningSeconds(),
            CreatedAt = _clock.UtcNow,
            RealPowerConfirmed = realPowerConfirmed
        };

        error = string.Empty;
        return true;
    }

    public void AppendActivity(string text)
    {
        RecentActivities.Insert(0, new RecentActivityItem(
            _clock.UtcNow.ToString("HH:mm:ss"),
            text));

        while (RecentActivities.Count > MaxRecentActivities)
        {
            RecentActivities.RemoveAt(RecentActivities.Count - 1);
        }
    }

    private async Task ExecuteCreateAsync()
    {
        if (!TryBuildDefinition(out var definition, out var error))
        {
            StatusMessage = error;
            AppendActivity("创建任务失败：" + error);
            return;
        }

        await SubmitCommandAsync(new CreateTaskCommand(definition), "创建任务");
    }

    private async Task ExecuteSnoozeAsync()
    {
        var instance = _currentInstance;
        if (instance is null)
        {
            return;
        }

        await SubmitCommandAsync(
            new SnoozeTaskCommand(instance.InstanceId, instance.StageToken, TimeSpan.FromMinutes(10)),
            "延迟10分钟");
    }

    private async Task ExecuteCancelAsync()
    {
        var instance = _currentInstance;
        if (instance is null)
        {
            return;
        }

        var confirmed = _cancelConfirmation is not null
            ? _cancelConfirmation()
            : System.Windows.MessageBox.Show(
                "取消后任务将停止执行，可在任务记录中查看。",
                "取消任务",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed)
        {
            return;
        }

        await SubmitCommandAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken),
            "取消任务");
    }

    private async Task ExecuteClearAsync()
    {
        var instance = _currentInstance;
        if (instance is null)
        {
            return;
        }

        await SubmitCommandAsync(
            new ClearTerminalTaskCommand(instance.InstanceId),
            "清除当前记录");
    }

    private void RowSnooze(object? parameter)
    {
        if (parameter is not TaskListItem item || _isSubmitting)
        {
            return;
        }

        _ = SubmitCommandAsync(
            new SnoozeTaskCommand(item.InstanceId, item.StageToken, TimeSpan.FromMinutes(10)),
            "延迟10分钟");
    }

    private void RowStop(object? parameter)
    {
        if (parameter is not TaskListItem item || _isSubmitting)
        {
            return;
        }

        var confirmed = _cancelConfirmation is not null
            ? _cancelConfirmation()
            : System.Windows.MessageBox.Show(
                "停止后任务将不再执行。",
                "停止任务",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed)
        {
            return;
        }

        _ = SubmitCommandAsync(
            new CancelTaskCommand(item.InstanceId, item.StageToken),
            "停止任务");
    }

    private void RowClear(object? parameter)
    {
        if (parameter is not TaskListItem item || _isSubmitting)
        {
            return;
        }

        _ = SubmitCommandAsync(
            new ClearTerminalTaskCommand(item.InstanceId),
            "清除记录");
    }

    private void ResolveArbitration(object? parameter)
    {
        if (parameter is not Guid taskId || _isSubmitting)
        {
            return;
        }

        _ = SubmitCommandAsync(
            new ResolveArbitrationCommand(taskId),
            "仲裁决策");
    }

    /// <summary>
    /// 由 SchedulerSnapshot.Instances 构建任务列表（仅消费快照，不拥有调度循环/持久化）。
    /// </summary>
    private void RefreshTaskList(SchedulerSnapshot snapshot, DateTimeOffset now)
    {
        var instances = snapshot.Instances.Values
            .OrderBy(instance => instance.ScheduledFireTime)
            .ThenBy(instance => instance.SourceTaskId)
            .ToList();

        TaskItems.Clear();
        foreach (var instance in instances)
        {
            var localFire = TimeZoneInfo.ConvertTime(instance.ScheduledFireTime, _clock.LocalTimeZone);
            var localWarning = instance.WarningStartTime is null
                ? (DateTimeOffset?)null
                : TimeZoneInfo.ConvertTime(instance.WarningStartTime.Value, _clock.LocalTimeZone);
            var remaining = instance.ScheduledFireTime - now;
            TaskItems.Add(new TaskListItem(
                instance.SourceTaskId,
                instance.InstanceId,
                instance.StageToken,
                UiTextMapper.Map(instance.ActionSnapshot),
                UiTextMapper.Map(instance.State),
                localFire.ToString("yyyy-MM-dd HH:mm:ss"),
                remaining > TimeSpan.Zero ? remaining.ToString(@"hh\:mm\:ss") : "已到期",
                localWarning?.ToString("HH:mm:ss") ?? "无",
                instance.State,
                instance.State is TaskInstanceState.Waiting or TaskInstanceState.Confirming,
                instance.State == TaskInstanceState.Waiting,
                instance.State is TaskInstanceState.Cancelled
                    or TaskInstanceState.Executed
                    or TaskInstanceState.Faulted
                    or TaskInstanceState.Interrupted));
        }

        HasTasks = instances.Count > 0;
        HasNoTasks = instances.Count == 0;
    }

    /// <summary>
    /// 呈现待决强制冲突（询问用户）与最近一次仲裁结果（赢家/合并/改期/理由）。
    /// </summary>
    private void RefreshArbitration(SchedulerSnapshot snapshot)
    {
        if (snapshot.PendingArbitration is { } pending)
        {
            PendingArbitrationItems.Clear();
            foreach (var taskId in pending.CandidateTaskIds)
            {
                if (!snapshot.Instances.TryGetValue(taskId, out var instance))
                {
                    continue;
                }

                var localFire = TimeZoneInfo.ConvertTime(instance.ScheduledFireTime, _clock.LocalTimeZone);
                PendingArbitrationItems.Add(new PendingArbitrationItem(
                    taskId,
                    UiTextMapper.Map(instance.ActionSnapshot),
                    localFire.ToString("yyyy-MM-dd HH:mm:ss")));
            }

            HasPendingArbitration = true;
        }
        else
        {
            PendingArbitrationItems.Clear();
            HasPendingArbitration = false;
        }

        if (snapshot.LastArbitration is { } outcome)
        {
            var parts = new List<string>();
            if (outcome.WinnerTaskId is { } winnerId)
            {
                parts.Add("赢家执行：" + DescribeTask(snapshot, winnerId));
            }

            if (outcome.MergedTaskIds.Count > 0)
            {
                parts.Add("合并：" + string.Join("、", outcome.MergedTaskIds.Select(id => DescribeTask(snapshot, id))));
            }

            if (outcome.RescheduledTaskIds.Count > 0)
            {
                parts.Add("改期：" + string.Join("、", outcome.RescheduledTaskIds.Select(id => DescribeTask(snapshot, id))));
            }

            var suffix = string.IsNullOrEmpty(outcome.DecisionReason)
                ? string.Empty
                : "（" + outcome.DecisionReason + "）";
            ArbitrationDecisionText = "多任务冲突已仲裁：" + string.Join("；", parts) + "。" + suffix;
        }
        else
        {
            ArbitrationDecisionText = string.Empty;
        }
    }

    /// <summary>启动链写入的崩溃恢复通知 → 恢复横幅。</summary>
    private void RefreshRecoveryNotice()
    {
        var notice = _recoveryNoticeService?.Notice;
        if (notice is not null && notice.InterruptedTaskIds.Count > 0)
        {
            RecoveryNoticeText = "上次异常退出后已恢复："
                + notice.InterruptedTaskIds.Count + " 个任务被标记为中断（不会补执行）："
                + string.Join("、", notice.InterruptedTaskIds.Select(ShortId)) + "。";
        }
        else
        {
            RecoveryNoticeText = string.Empty;
        }
    }

    private static string DescribeTask(SchedulerSnapshot snapshot, Guid taskId)
    {
        if (snapshot.Instances.TryGetValue(taskId, out var instance))
        {
            return UiTextMapper.Map(instance.ActionSnapshot) + "（" + ShortId(taskId) + "）";
        }

        return ShortId(taskId);
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8].ToUpperInvariant();

    private async Task SubmitCommandAsync(SchedulerCommand command, string displayName)
    {
        if (_isSubmitting)
        {
            return;
        }

        LogCommandRequested(command);
        _isSubmitting = true;
        RaiseAllCommands();

        try
        {
            var result = await _engine.SubmitAsync(command, CancellationToken.None);
            if (result.Succeeded)
            {
                LogCommandAccepted(command);
                StatusMessage = displayName + "已提交";
                AppendActivity(displayName + "成功");
            }
            else
            {
                LogCommandRejected(command, result);
                var reason = UiTextMapper.MapCommand(result.Status);
                StatusMessage = displayName + "失败：" + reason;
                AppendActivity(displayName + "失败：" + reason);
            }
        }
        catch (Exception exception)
        {
            TryLog(logger => logger.Warning("TaskCommandRejected", "命令提交异常：" + displayName + "。"));
            StatusMessage = displayName + "提交异常：" + exception.Message;
            AppendActivity(displayName + "提交异常");
        }
        finally
        {
            _isSubmitting = false;
            RaiseAllCommands();
            Refresh(GetSnapshot(), _clock.UtcNow);
        }
    }

    private void LogCommandRequested(SchedulerCommand command)
    {
        switch (command)
        {
            case CreateTaskCommand create:
                TryLog(logger => logger.Info(
                    "TaskCreateRequested",
                    "请求创建任务：" + create.Definition.Kind
                        + "，动作：" + create.Definition.Action
                        + "，提醒秒数：" + create.Definition.WarningSeconds + "。"));
                break;

            case SnoozeTaskCommand snooze:
                TryLog(logger => logger.Info(
                    "TaskSnoozeRequested",
                    "请求延迟任务：" + snooze.Duration.TotalMinutes + " 分钟。"));
                break;

            case CancelTaskCommand:
                TryLog(logger => logger.Info("TaskCancelRequested", "请求取消任务。"));
                break;

            case ClearTerminalTaskCommand:
                TryLog(logger => logger.Info("TaskClearRequested", "请求清除任务记录。"));
                break;
        }
    }

    private void LogCommandAccepted(SchedulerCommand command)
    {
        switch (command)
        {
            case CreateTaskCommand:
                TryLog(logger => logger.Info("TaskCreateAccepted", "创建任务已接受。"));
                break;

            case SnoozeTaskCommand:
                TryLog(logger => logger.Info("TaskSnoozeAccepted", "延迟任务已接受。"));
                break;

            case CancelTaskCommand:
                TryLog(logger => logger.Info("TaskCancelAccepted", "取消任务已接受。"));
                break;

            case ClearTerminalTaskCommand:
                TryLog(logger => logger.Info("TaskCleared", "任务记录已清除。"));
                break;
        }
    }

    private void LogCommandRejected(SchedulerCommand command, SchedulerCommandResult result)
    {
        switch (command)
        {
            case CreateTaskCommand:
                TryLog(logger => logger.Warning(
                    "TaskCreateRejected",
                    "创建任务被拒绝，结果：" + result.Status + "。"));
                break;

            case SnoozeTaskCommand:
                TryLog(logger => logger.Warning(
                    "TaskSnoozeRejected",
                    "延迟任务被拒绝，结果：" + result.Status + "。"));
                break;

            case CancelTaskCommand:
                TryLog(logger => logger.Warning(
                    "TaskCancelRejected",
                    "取消任务被拒绝，结果：" + result.Status + "。"));
                break;

            case ClearTerminalTaskCommand:
                TryLog(logger => logger.Warning(
                    "TaskClearRejected",
                    "清除任务记录被拒绝，结果：" + result.Status + "。"));
                break;
        }
    }

    private void TryLog(Action<IApplicationLogger> log)
    {
        try
        {
            log(_logger);
        }
        catch
        {
            // Logging must never affect business state or results.
        }
    }

    private void CopyLogPath()
    {
        try
        {
            System.Windows.Clipboard.SetText(_logger.LogDirectory);
            StatusMessage = "日志路径已复制到剪贴板";
            AppendActivity("已复制日志路径");
        }
        catch (Exception exception)
        {
            StatusMessage = "复制日志路径失败：" + exception.Message;
        }
    }

    private void RefreshCreateState()
    {
        var canCreate = CanCreateNow(out var reason);
        CreateDisabledReason = canCreate ? string.Empty : reason;
        OnPropertyChanged(nameof(CreateDisabledReason));
        CreateCommand.RaiseCanExecuteChanged();
    }

    private void RaiseAllCommands()
    {
        CreateCommand.RaiseCanExecuteChanged();
        SnoozeCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private SchedulerSnapshot GetSnapshot()
    {
        try
        {
            return _engine.GetSnapshot();
        }
        catch (Exception)
        {
            return SchedulerSnapshot.Empty;
        }
    }

    /// <summary>
    /// 从多实例快照中选择一个「主实例」供单任务仪表盘展示（完整列表交互归 T08）。
    /// 优先最早到期的非终态实例；无则回退到唯一的终态实例；再否则为 null。
    /// </summary>
    private static TaskInstance? SelectPrimaryInstance(SchedulerSnapshot snapshot)
    {
        var instances = snapshot.Instances.Values.ToList();
        if (instances.Count == 0)
        {
            return null;
        }

        var active = instances
            .Where(instance => !IsTerminal(instance.State))
            .OrderBy(instance => instance.ScheduledFireTime)
            .FirstOrDefault();
        if (active is not null)
        {
            return active;
        }

        return instances.Count == 1 ? instances[0] : null;
    }

    private static bool IsTerminal(TaskInstanceState state)
        => state is TaskInstanceState.Cancelled
            or TaskInstanceState.Executed
            or TaskInstanceState.Faulted
            or TaskInstanceState.Interrupted;

    private int GetWarningSeconds() => ReminderIndex switch
    {
        1 => 60,
        2 => 300,
        3 => 600,
        4 => 1800,
        _ => 0
    };

    private static bool TryParseRange(
        string text,
        int min,
        int max,
        string unit,
        out int value,
        out string error)
    {
        var normalized = text
            .Replace(unit, string.Empty, StringComparison.Ordinal)
            .Trim();

        if (!int.TryParse(normalized, out value))
        {
            error = $"请输入有效的{unit}数值";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{unit}需在 {min}–{max} 之间";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
