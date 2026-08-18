using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Diagnostics;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Unattended;

namespace AutoShutdown.App.Presentation;

public enum TimeMode
{
    Countdown = 0,
    TodayAt = 1,
    DailyAt = 2,

    /// <summary>每周工作日（周一~周五可选）固定时刻，可叠加节假日例外。</summary>
    Weekdays = 3,

    /// <summary>下个工作日固定时刻（一次性）。</summary>
    NextWorkday = 4,

    /// <summary>每月第 N 个工作日固定时刻（周期）。</summary>
    NthWorkdayOfMonth = 5,

    /// <summary>一次性指定日期时间（过期即终结，不追溯）。</summary>
    OneTime = 6,

    /// <summary>空闲触发：输入持续空闲达阈值即触发倒计时（S15）。</summary>
    Idle = 7
}

public sealed record NavItem(string Title, string Icon, string PageKey);

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
    string TriggerText,
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
    private readonly Func<bool>? _closeAppsForceKillConfirmation;
    private readonly IUnattendedPolicyService? _unattendedPolicy;
    private readonly Func<bool>? _unattendedEnableConfirmation;
    private readonly Func<bool>? _unattendedEnableSecondConfirmation;
    private readonly RecoveryNoticeService? _recoveryNoticeService;
    private readonly WolTargetsSectionViewModel? _wolTargetsSection;
    private readonly RtcStatusSectionViewModel? _rtcStatusSection;
    private readonly TaskSyncSectionViewModel? _taskSyncSection;
    private readonly RemoteSectionViewModel? _remoteSection;
    private readonly DiagnosticsCenterViewModel? _diagnosticsCenter;

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
    private AppConfig? _loadedConfig;

    public MainWindowViewModel(
        ISchedulerEngine engine,
        IConfigurationService configurationService,
        IClock clock,
        IApplicationLogger logger,
        IAutoStartService autoStart,
        Func<bool>? autoStartConfirmation = null,
        Func<bool>? cancelConfirmation = null,
        Func<bool>? realPowerConfirmation = null,
        Func<bool>? closeAppsForceKillConfirmation = null,
        IUnattendedPolicyService? unattendedPolicy = null,
        Func<bool>? unattendedEnableConfirmation = null,
        Func<bool>? unattendedEnableSecondConfirmation = null,
        RecoveryNoticeService? recoveryNotice = null,
        WolTargetsSectionViewModel? wolTargetsSection = null,
        RtcStatusSectionViewModel? rtcStatusSection = null,
        TaskSyncSectionViewModel? taskSyncSection = null,
        RemoteSectionViewModel? remoteSection = null,
        DiagnosticsCenterViewModel? diagnosticsCenter = null)
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
        _closeAppsForceKillConfirmation = closeAppsForceKillConfirmation;
        _unattendedPolicy = unattendedPolicy;
        _unattendedEnableConfirmation = unattendedEnableConfirmation;
        _unattendedEnableSecondConfirmation = unattendedEnableSecondConfirmation;
        _recoveryNoticeService = recoveryNotice;
        _wolTargetsSection = wolTargetsSection;
        _rtcStatusSection = rtcStatusSection;
        _taskSyncSection = taskSyncSection;
        _remoteSection = remoteSection;
        _diagnosticsCenter = diagnosticsCenter;

        NavItems =
        [
            new NavItem("首页", "⌂", "home"),
            new NavItem("任务管理", "▤", "tasks"),
            new NavItem("高级功能", "◈", "advanced"),
            new NavItem("网络唤醒", "⇪", "wol"),
            new NavItem("日志与诊断", "▤", "logs"),
            new NavItem("软件设置", "⚙", "settings"),
            new NavItem("关于软件", "ℹ", "about")
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
        AddCloseAppsTargetCommand = new RelayCommand(AddCloseAppsTarget);
        RemoveCloseAppsTargetCommand = new RelayCommand(parameter =>
        {
            if (parameter is CloseAppsTargetRow row)
            {
                CloseAppsTargets.Remove(row);
            }
        });
        SaveCloseAppsCommand = new AsyncRelayCommand(ExecuteSaveCloseAppsAsync);
        EnableUnattendedCommand = new AsyncRelayCommand(
            ExecuteEnableUnattendedAsync,
            () => !IsUnattendedBusy && !_unattendedAuthorized && _unattendedPolicy is not null);
        RevokeUnattendedCommand = new AsyncRelayCommand(
            ExecuteRevokeUnattendedAsync,
            () => !IsUnattendedBusy && _unattendedAuthorized && _unattendedPolicy is not null);
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
                OnPropertyChanged(nameof(IsTasksPageVisible));
                OnPropertyChanged(nameof(IsAdvancedPageVisible));
                OnPropertyChanged(nameof(IsWolPageVisible));
                OnPropertyChanged(nameof(IsLogsPageVisible));
                OnPropertyChanged(nameof(IsSettingsPageVisible));
                OnPropertyChanged(nameof(IsAboutPageVisible));
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

    public bool IsTasksPageVisible => SelectedNav.PageKey == "tasks";

    public bool IsAdvancedPageVisible => SelectedNav.PageKey == "advanced";

    public bool IsWolPageVisible => SelectedNav.PageKey == "wol";

    public bool IsLogsPageVisible => SelectedNav.PageKey == "logs";

    public bool IsSettingsPageVisible => SelectedNav.PageKey == "settings";

    public bool IsAboutPageVisible => SelectedNav.PageKey == "about";

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
                OnPropertyChanged(nameof(ModeIsWeekdays));
                OnPropertyChanged(nameof(ModeIsNextWorkday));
                OnPropertyChanged(nameof(ModeIsNthWorkdayOfMonth));
                OnPropertyChanged(nameof(ModeIsOneTime));
                OnPropertyChanged(nameof(ModeIsIdle));
                OnPropertyChanged(nameof(IsWeekdaySelectorVisible));
                OnPropertyChanged(nameof(IsNthWorkdaySelectorVisible));
                OnPropertyChanged(nameof(IsOneTimeDateVisible));
                OnPropertyChanged(nameof(IsIdleSelectorVisible));
                OnPropertyChanged(nameof(IsTimeInputVisible));
                OnPropertyChanged(nameof(IsHolidayInputVisible));
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

    public bool ModeIsWeekdays
    {
        get => SelectedMode == TimeMode.Weekdays;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.Weekdays;
            }
        }
    }

    public bool ModeIsNextWorkday
    {
        get => SelectedMode == TimeMode.NextWorkday;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.NextWorkday;
            }
        }
    }

    public bool ModeIsNthWorkdayOfMonth
    {
        get => SelectedMode == TimeMode.NthWorkdayOfMonth;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.NthWorkdayOfMonth;
            }
        }
    }

    public bool ModeIsOneTime
    {
        get => SelectedMode == TimeMode.OneTime;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.OneTime;
            }
        }
    }

    public bool ModeIsIdle
    {
        get => SelectedMode == TimeMode.Idle;
        set
        {
            if (value)
            {
                SelectedMode = TimeMode.Idle;
            }
        }
    }

    public bool IsWeekdaySelectorVisible => SelectedMode == TimeMode.Weekdays;

    public bool IsNthWorkdaySelectorVisible => SelectedMode == TimeMode.NthWorkdayOfMonth;

    public bool IsOneTimeDateVisible => SelectedMode == TimeMode.OneTime;

    /// <summary>空闲阈值输入仅对空闲触发规则显示（S15）。</summary>
    public bool IsIdleSelectorVisible => SelectedMode == TimeMode.Idle;

    /// <summary>目标时间输入对除倒计时与空闲触发外的规则显示（空闲不设目标时刻）。</summary>
    public bool IsTimeInputVisible => SelectedMode is not TimeMode.Countdown and not TimeMode.Idle;

    /// <summary>节假日例外输入仅对支持节假日的规则显示（DailyAt/Weekdays/NextWorkday/NthWorkdayOfMonth）。</summary>
    public bool IsHolidayInputVisible
        => SelectedMode is TimeMode.DailyAt or TimeMode.Weekdays or TimeMode.NextWorkday or TimeMode.NthWorkdayOfMonth;

    public IReadOnlyList<string> CountdownHourOptions { get; } =
        Enumerable.Range(0, 169).Select(value => $"{value:00} 小时").ToArray();

    public IReadOnlyList<string> ClockHourOptions { get; } =
        Enumerable.Range(0, 24).Select(value => $"{value:00} 小时").ToArray();

    public IReadOnlyList<string> MinuteOptions { get; } =
        Enumerable.Range(0, 60).Select(value => $"{value:00} 分钟").ToArray();

    public IReadOnlyList<string> SecondOptions { get; } =
        Enumerable.Range(0, 60).Select(value => $"{value:00} 秒").ToArray();

    // ---- S15 空闲阈值输入 ----

    /// <summary>空闲触发阈值选项：索引 0 = 继承全局默认；后续为显式阈值（秒）。</summary>
    public IReadOnlyList<string> IdleThresholdOptions { get; } =
    [
        "继承全局默认（30 分钟）",
        "空闲 1 分钟",
        "空闲 5 分钟",
        "空闲 10 分钟",
        "空闲 15 分钟",
        "空闲 30 分钟",
        "空闲 1 小时",
        "空闲 2 小时",
        "空闲 3 小时",
        "空闲 6 小时",
        "空闲 12 小时",
        "空闲 24 小时",
        "空闲 7 天"
    ];

    private int _idleThresholdIndex;

    public int IdleThresholdIndex
    {
        get => _idleThresholdIndex;
        set
        {
            if (SetProperty(ref _idleThresholdIndex, value))
            {
                RefreshCreateState();
            }
        }
    }

    /// <summary>空闲阈值选项索引 → 秒；索引 0（继承全局默认）返回 null。</summary>
    private static int? IdleThresholdSecondsForIndex(int index) => index switch
    {
        1 => 60,
        2 => 300,
        3 => 600,
        4 => 900,
        5 => 1800,
        6 => 3600,
        7 => 7200,
        8 => 10800,
        9 => 21600,
        10 => 43200,
        11 => 86400,
        12 => 604800,
        _ => null
    };

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

    // ---- S14 复杂排程输入（Weekdays / NthWorkdayOfMonth / OneTime / 节假日例外） ----

    private bool _weekdayMonday = true;
    private bool _weekdayTuesday = true;
    private bool _weekdayWednesday = true;
    private bool _weekdayThursday = true;
    private bool _weekdayFriday = true;

    public bool WeekdayMonday
    {
        get => _weekdayMonday;
        set
        {
            if (SetProperty(ref _weekdayMonday, value))
            {
                RefreshCreateState();
            }
        }
    }

    public bool WeekdayTuesday
    {
        get => _weekdayTuesday;
        set
        {
            if (SetProperty(ref _weekdayTuesday, value))
            {
                RefreshCreateState();
            }
        }
    }

    public bool WeekdayWednesday
    {
        get => _weekdayWednesday;
        set
        {
            if (SetProperty(ref _weekdayWednesday, value))
            {
                RefreshCreateState();
            }
        }
    }

    public bool WeekdayThursday
    {
        get => _weekdayThursday;
        set
        {
            if (SetProperty(ref _weekdayThursday, value))
            {
                RefreshCreateState();
            }
        }
    }

    public bool WeekdayFriday
    {
        get => _weekdayFriday;
        set
        {
            if (SetProperty(ref _weekdayFriday, value))
            {
                RefreshCreateState();
            }
        }
    }

    public IReadOnlyList<string> NthWorkdayOptions { get; } =
        Enumerable.Range(1, 23).Select(value => $"第 {value} 个工作日").ToArray();

    private int _nthWorkdayIndex;

    public int NthWorkdayIndex
    {
        get => _nthWorkdayIndex;
        set
        {
            if (SetProperty(ref _nthWorkdayIndex, value))
            {
                RefreshCreateState();
            }
        }
    }

    private DateTime? _oneTimeDate;

    public DateTime? OneTimeDate
    {
        get => _oneTimeDate;
        set
        {
            if (SetProperty(ref _oneTimeDate, value))
            {
                RefreshCreateState();
            }
        }
    }

    private string _holidayDatesText = string.Empty;

    public string HolidayDatesText
    {
        get => _holidayDatesText;
        set
        {
            if (SetProperty(ref _holidayDatesText, value))
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
                OnPropertyChanged(nameof(ActionIsWakeOnLan));
                OnPropertyChanged(nameof(IsWolTargetSelectorVisible));
                // S20-D1：动作变化会改变「使用无人值守」是否可选（授权需与动作匹配）。
                // S21：WoL 不是电源动作，不显示无人值守选项。
                _ = RefreshUnattendedTaskAvailabilityAsync();
                RefreshCreateState();
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

    /// <summary>唤醒他机（S21）：经调度器作为显式 WoL 任务触发，不经双闸门/真实电源。</summary>
    public bool ActionIsWakeOnLan
    {
        get => SelectedAction == PowerAction.WakeOnLan;
        set
        {
            if (value)
            {
                SelectedAction = PowerAction.WakeOnLan;
            }
        }
    }

    /// <summary>WoL 目标机器选择器是否可见（仅「唤醒他机」动作时显示）。</summary>
    public bool IsWolTargetSelectorVisible => SelectedAction == PowerAction.WakeOnLan;

    private Guid? _selectedWolTargetId;

    /// <summary>
    /// WoL 任务选中的目标机器 id（S21）。仅向用户显式配置的局域网目标发送；
    /// 未选择则无法创建 WoL 任务（fail-closed）。
    /// </summary>
    public Guid? SelectedWolTargetId
    {
        get => _selectedWolTargetId;
        set
        {
            if (SetProperty(ref _selectedWolTargetId, value))
            {
                RefreshCreateState();
            }
        }
    }

    // ---- S21 设置页分区视图模型（由组合根注入；测试可缺省为 null → 分区默认安全空态） ----

    /// <summary>Wake-on-LAN 目标机器管理分区（设置页）。</summary>
    public WolTargetsSectionViewModel? WolTargetsSection => _wolTargetsSection;

    /// <summary>一次性 RTC 唤醒能力状态分区（设置页）。</summary>
    public RtcStatusSectionViewModel? RtcStatusSection => _rtcStatusSection;

    /// <summary>Windows 任务计划程序单向同步分区（设置页；S22）。</summary>
    public TaskSyncSectionViewModel? TaskSyncSection => _taskSyncSection;

    /// <summary>局域网远程控制分区（设置页；S23）。</summary>
    public RemoteSectionViewModel? RemoteSection => _remoteSection;

    /// <summary>日志与诊断中心（S-UI1）。</summary>
    public DiagnosticsCenterViewModel? DiagnosticsCenter => _diagnosticsCenter;

    /// <summary>
    /// 从当前 VM 状态汇总诊断快照（供诊断中心的自检/导出使用）。只读，不触发任何副作用；
    /// 隐私字段是否包含由导出层依据 <see cref="DiagnosticsCenterViewModel.IncludePrivacyInfo"/> 决定，
    /// 这里始终回传原始值，PIN 值永不进入快照。
    /// </summary>
    public DiagnosticsLiveSummary BuildDiagnosticsLiveSummary()
    {
        var tasks = TaskItems
            .Select(t => new DiagnosticsTaskRow(
                KindText: t.TriggerText,
                ActionText: t.ActionText,
                FireTimeText: t.FireTimeText,
                StateText: t.StateText,
                ExtraText: t.CountdownText))
            .ToList();

        var wolTargets = (_wolTargetsSection?.Targets ?? [])
            .Select(t => new DiagnosticsWolTargetRow(
                Name: t.Name,
                Mac: t.Mac,
                BroadcastText: t.BroadcastText ?? string.Empty,
                PortText: t.Port?.ToString() ?? string.Empty))
            .ToList();

        var remoteDevices = (_remoteSection?.Devices ?? Array.Empty<RemoteDeviceRow>())
            .Select(d => new DiagnosticsPairedDevice(
                Name: d.DeviceName,
                DeviceId: d.DeviceId,
                PairedAtText: d.PairedAtText))
            .ToList();

        var pinPresence = _remoteSection is null
            ? "无"
            : _remoteSection.IsLocked
                ? "锁定中"
                : string.IsNullOrWhiteSpace(_remoteSection.PinDisplay)
                    ? "无"
                    : "已生成（值不导出）";

        var listenPort = _remoteSection is not null
            && int.TryParse(_remoteSection.ListenPortText, out var parsedPort)
            ? parsedPort
            : (int?)null;

        return new DiagnosticsLiveSummary(
            VersionText: VersionText,
            BuildCommitText: AppInfo.BuildCommitText,
            SigningStatusText: AppInfo.SigningStatusText,
            HeaderModeText: HeaderModeText,
            ConfigStatusText: ConfigStatusText,
            ConfigUsable: !IsConfigInitVisible,
            SchedulerStatusText: SchedulerStatusText,
            DataRoot: Infrastructure.DataRootResolver.Resolve(),
            LogDirectory: _logger.LogDirectory,
            Tasks: tasks,
            WolTargets: wolTargets,
            TaskSyncHealthy: _taskSyncSection is null || !_taskSyncSection.HasError,
            TaskSyncStatusText: _taskSyncSection?.StatusText ?? string.Empty,
            TaskSyncDetailText: _taskSyncSection?.DetailText ?? string.Empty,
            RemoteEnabled: _remoteSection?.Enabled ?? false,
            RemoteListenAddress: _remoteSection?.ListenAddress ?? string.Empty,
            RemoteListenPort: listenPort,
            RemoteRequireTls: _remoteSection?.RequireTls ?? false,
            RemotePinPresenceText: pinPresence,
            RemoteDevices: remoteDevices);
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

    private string _countdownSourceText = "—";

    /// <summary>倒计时来源：空闲触发 vs 定时排程（S15）。</summary>
    public string CountdownSourceText
    {
        get => _countdownSourceText;
        private set => SetProperty(ref _countdownSourceText, value);
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

    /// <summary>
    /// 顶部版本/构建信息（S-PKG 发布追溯最小入口）。读取程序集 InformationalVersion
    /// （发布时由构建脚本注入 v2.0.0-S{STEP}.{BUILD}，SDK 附加的 +{commit} 后缀被裁掉），
    /// 与 manifest / Git 提交 / SHA-256 对应；未注入时显示默认串。
    /// </summary>
    public string VersionText
    {
        get
        {
            var informational = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(MainWindowViewModel).Assembly)
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
            {
                return "v1.0.0-dev";
            }

            var plusIndex = informational.IndexOf('+');
            var version = plusIndex >= 0 ? informational[..plusIndex] : informational;
            return version.StartsWith('v') ? version : "v" + version;
        }
    }

    // ---- 关于软件（S-UI1）----

    /// <summary>产品名。</summary>
    public string ProductNameText => AppInfo.ProductName;

    /// <summary>构建提交（InformationalVersion 中 + 之后的部分）。</summary>
    public string BuildCommitText => AppInfo.BuildCommitText;

    /// <summary>候选包签名状态（运行时 Authenticode 探测，如实标示 unsigned-candidate）。</summary>
    public string SigningStatusText => AppInfo.SigningStatusText;

    /// <summary>数据目录（AUTOSHUTDOWN_DATA_ROOT 覆盖或默认 %LocalAppData%\AutoShutdown）。</summary>
    public string DataDirectoryText => AppInfo.DataDirectory;

    /// <summary>关于页：隐私与安全边界说明（静态文案）。</summary>
    public const string PrivacyBoundaryText =
        "· 全部数据（配置、任务、日志、WoL 目标、远程配对）只保存在本机数据目录，绝不上传任何服务器。\n"
        + "· 远程控制默认关闭且默认只读；即使启用也只监听你配置的地址与端口，并强制 TLS。\n"
        + "· 诊断包仅在你在「日志与诊断」页主动导出时生成，且默认脱敏（PIN/密钥/私钥/PFX 密码绝不带出）。\n"
        + "· 截图仅在你在「日志与诊断」页主动保存当前窗口时写入你选择的路径，无自动上传、无远程桌面、无后台外传。\n"
        + "· 真实电源操作仅在你关闭测试模式且显式开启后才会执行（双闸门）；默认全程安全测试模式隔离。";

    /// <summary>关于页：帮助与反馈说明（静态文案）。</summary>
    public const string HelpFeedbackText =
        "· 使用问题：先查看「日志与诊断」页的日志目录与安全自检；诊断包可在需要时主动导出。\n"
        + "· 任务计划同步/远程控制的已知限制与风险见「高级功能」「软件设置」各卡片说明。\n"
        + "· 反馈请随附版本号（见本页「版本」）与诊断摘要；候选包未签名，首次运行可能触发 SmartScreen，属预期。";

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
        private set
        {
            if (SetProperty(ref _createDisabledReason, value))
            {
                OnPropertyChanged(nameof(HasCreateError));
            }
        }
    }

    /// <summary>创建按钮禁用原因是否可见（作为表单错误提示）。</summary>
    public bool HasCreateError => !string.IsNullOrEmpty(_createDisabledReason);

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

    public ICommand AddCloseAppsTargetCommand { get; }

    public ICommand RemoveCloseAppsTargetCommand { get; }

    public AsyncRelayCommand SaveCloseAppsCommand { get; }

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

    // ---- 无人值守（S20） ----

    public const string UnattendedRiskText =
        "无人值守默认关闭。启用后，任务到期将不再等待人工确认，自动执行所选动作的电源操作（关机/重启/睡眠/休眠）。\n"
        + "这会绕过人工确认闸门（仍受配置 RealPowerEnabled 与 Pre-Pipeline 双重闸门约束），存在未保存工作丢失风险，请谨慎启用。";

    private bool _unattendedAuthorized;
    private bool _isUnattendedBusy;

    private string _unattendedStatusText = "默认关闭（未启用）";

    private string _unattendedDetailText = "未找到无人值守授权记录，默认不执行真实电源（fail-closed）。";

    private string _unattendedErrorText = string.Empty;

    public bool IsUnattendedBusy
    {
        get => _isUnattendedBusy;
        private set
        {
            if (SetProperty(ref _isUnattendedBusy, value))
            {
                EnableUnattendedCommand.RaiseCanExecuteChanged();
                RevokeUnattendedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string UnattendedStatusText
    {
        get => _unattendedStatusText;
        private set => SetProperty(ref _unattendedStatusText, value);
    }

    public string UnattendedDetailText
    {
        get => _unattendedDetailText;
        private set => SetProperty(ref _unattendedDetailText, value);
    }

    public string UnattendedErrorText
    {
        get => _unattendedErrorText;
        private set
        {
            if (SetProperty(ref _unattendedErrorText, value))
            {
                OnPropertyChanged(nameof(HasUnattendedError));
            }
        }
    }

    public bool HasUnattendedError => !string.IsNullOrEmpty(_unattendedErrorText);

    public bool IsUnattendedEnabled => _unattendedAuthorized;

    // ---- S20-D1：任务级「使用无人值守」选择 ----

    private bool _useUnattended;
    private bool _unattendedAuthorizedForAction;

    /// <summary>
    /// 任务级「使用无人值守」选择（S20-D1）。默认关闭。选中后创建 RealPowerConfirmed=false
    /// 的任务，由调度器在倒计时边界做无人值守等效确认裁决；未选中维持现有人工确认路径。
    /// </summary>
    public bool UseUnattended
    {
        get => _useUnattended;
        set
        {
            if (SetProperty(ref _useUnattended, value))
            {
                RefreshCreateState();
            }
        }
    }

    /// <summary>任务级「使用无人值守」选项是否可见：仅真实电源模式显示。</summary>
    // S21：WoL 不是电源动作（不经双闸门），不显示/不允许无人值守选项。
    public bool IsUnattendedTaskOptionVisible
        => _configRealPowerEnabled && SelectedAction != PowerAction.WakeOnLan;

    /// <summary>
    /// 任务级「使用无人值守」选项是否可选：真实电源模式 + 本地无人值守授权有效且与所选动作匹配。
    /// 授权失效/动作不匹配/策略异常一律不可选（fail-closed）。
    /// </summary>
    public bool IsUnattendedTaskOptionAvailable
        => _configRealPowerEnabled && SelectedAction != PowerAction.WakeOnLan && _unattendedAuthorizedForAction;

    /// <summary>任务级「使用无人值守」选项的禁用/可用提示。</summary>
    public string UnattendedTaskOptionHint
    {
        get
        {
            if (!_configRealPowerEnabled)
            {
                return "仅真实电源模式下可用无人值守。";
            }

            return _unattendedAuthorizedForAction
                ? "本地无人值守授权有效，选中后任务到期将不再等待人工确认。"
                : "本地无人值守授权未生效或与所选动作不匹配，暂不可选。";
        }
    }

    /// <summary>
    /// 刷新任务级「使用无人值守」的可选状态：评估本地无人值守授权对当前所选动作是否有效。
    /// 失败/异常一律 fail-closed（不可选），并在不可选时强制关闭选择。
    /// </summary>
    public async Task RefreshUnattendedTaskAvailabilityAsync()
    {
        if (_unattendedPolicy is null || !_configRealPowerEnabled)
        {
            _unattendedAuthorizedForAction = false;
        }
        else
        {
            try
            {
                var decision = await _unattendedPolicy.EvaluateAsync(SelectedAction, CancellationToken.None);
                _unattendedAuthorizedForAction = decision.IsAuthorized;
            }
            catch
            {
                _unattendedAuthorizedForAction = false;
            }
        }

        if (!IsUnattendedTaskOptionAvailable && _useUnattended)
        {
            _useUnattended = false;
            OnPropertyChanged(nameof(UseUnattended));
        }

        OnPropertyChanged(nameof(IsUnattendedTaskOptionAvailable));
        OnPropertyChanged(nameof(UnattendedTaskOptionHint));
        RefreshCreateState();
    }

    public AsyncRelayCommand EnableUnattendedCommand { get; }

    public AsyncRelayCommand RevokeUnattendedCommand { get; }

    public async Task RefreshUnattendedAsync()
    {
        if (_unattendedPolicy is null)
        {
            SetUnattended(false, "不可用", "无人值守策略服务未注册（当前为安全环境）。");
            await RefreshUnattendedTaskAvailabilityAsync();
            return;
        }

        UnattendedAuthorizationDecision decision;
        try
        {
            decision = await _unattendedPolicy.EvaluateAsync(PowerAction.Shutdown, CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetUnattended(false, "不可用", "无人值守策略评估失败（fail-closed）：" + exception.Message);
            await RefreshUnattendedTaskAvailabilityAsync();
            return;
        }

        SetUnattended(
            decision.IsAuthorized,
            MapUnattendedStatus(decision.Status),
            BuildUnattendedDetail(decision));

        await RefreshUnattendedTaskAvailabilityAsync();
    }

    private void SetUnattended(bool authorized, string status, string detail)
    {
        _unattendedAuthorized = authorized;
        UnattendedStatusText = status;
        UnattendedDetailText = detail;
        OnPropertyChanged(nameof(IsUnattendedEnabled));
        EnableUnattendedCommand.RaiseCanExecuteChanged();
        RevokeUnattendedCommand.RaiseCanExecuteChanged();
    }

    private async Task ExecuteEnableUnattendedAsync()
    {
        if (IsUnattendedBusy || _unattendedPolicy is null)
        {
            return;
        }

        // 本地双重确认：第一次说明风险，第二次为强制二次确认（S20 硬性要求）。
        if (!ConfirmUnattendedEnable(_unattendedEnableConfirmation, "第一次确认"))
        {
            AppendActivity("已取消启用无人值守");
            return;
        }

        if (!ConfirmUnattendedEnable(_unattendedEnableSecondConfirmation, "第二次确认"))
        {
            AppendActivity("已取消启用无人值守（未完成二次确认）");
            return;
        }

        IsUnattendedBusy = true;
        UnattendedErrorText = string.Empty;

        try
        {
            var result = await _unattendedPolicy.EnableAsync(
                new UnattendedEnableRequest
                {
                    Action = PowerAction.Shutdown,
                    TriggerReason = "本地控制面板启用无人值守",
                    SecondConfirmationCompleted = true
                },
                CancellationToken.None);

            if (result.Succeeded)
            {
                TryLog(logger => logger.Info("UnattendedEnabled", "无人值守已启用。"));
                StatusMessage = "无人值守已启用";
                AppendActivity("已启用无人值守（版本 V" + result.Policy!.AuthorizationVersion + "）");
            }
            else
            {
                UnattendedErrorText = MapUnattendedEnableError(result.Status, result.Error);
                StatusMessage = "无人值守启用失败：" + UnattendedErrorText;
                AppendActivity("无人值守启用失败：" + UnattendedErrorText);
            }
        }
        catch (Exception exception)
        {
            UnattendedErrorText = "无人值守启用异常：" + exception.Message;
            StatusMessage = UnattendedErrorText;
            AppendActivity(UnattendedErrorText);
        }
        finally
        {
            await RefreshUnattendedAsync();
            IsUnattendedBusy = false;
        }
    }

    private async Task ExecuteRevokeUnattendedAsync()
    {
        if (IsUnattendedBusy || _unattendedPolicy is null)
        {
            return;
        }

        IsUnattendedBusy = true;
        UnattendedErrorText = string.Empty;

        try
        {
            var result = await _unattendedPolicy.RevokeAsync(CancellationToken.None);
            if (result.Succeeded)
            {
                TryLog(logger => logger.Info("UnattendedRevoked", "无人值守已撤销。"));
                StatusMessage = "无人值守已撤销";
                AppendActivity("已撤销无人值守授权");
            }
            else
            {
                UnattendedErrorText = "撤销失败：" + result.Error;
                StatusMessage = UnattendedErrorText;
                AppendActivity(UnattendedErrorText);
            }
        }
        catch (Exception exception)
        {
            UnattendedErrorText = "无人值守撤销异常：" + exception.Message;
            StatusMessage = UnattendedErrorText;
            AppendActivity(UnattendedErrorText);
        }
        finally
        {
            await RefreshUnattendedAsync();
            IsUnattendedBusy = false;
        }
    }

    private bool ConfirmUnattendedEnable(Func<bool>? confirmation, string step)
    {
        if (confirmation is not null)
        {
            return confirmation();
        }

        return System.Windows.MessageBox.Show(
            UnattendedRiskText + "\n\n（" + step + "）确认启用无人值守？",
            "启用无人值守",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private static string MapUnattendedStatus(UnattendedPolicyStatus status) => status switch
    {
        UnattendedPolicyStatus.NotFound => "默认关闭（未启用）",
        UnattendedPolicyStatus.Disabled => "已禁用",
        UnattendedPolicyStatus.Revoked => "已撤销",
        UnattendedPolicyStatus.Expired => "已过期",
        UnattendedPolicyStatus.Authorized => "已授权（无人值守生效）",
        UnattendedPolicyStatus.ActionMismatch => "已授权但动作不匹配",
        UnattendedPolicyStatus.Corrupt => "授权记录损坏（拒绝执行）",
        UnattendedPolicyStatus.Invalid => "授权记录非法（拒绝执行）",
        UnattendedPolicyStatus.UnsupportedVersion => "授权版本不受支持（拒绝执行）",
        UnattendedPolicyStatus.Unavailable => "授权记录不可用（拒绝执行）",
        _ => "未知状态（拒绝执行）"
    };

    private static string BuildUnattendedDetail(UnattendedAuthorizationDecision decision)
    {
        var policy = decision.Policy;
        if (policy is null)
        {
            return decision.Status switch
            {
                UnattendedPolicyStatus.NotFound => "未找到授权记录，默认不执行真实电源（fail-closed）。",
                UnattendedPolicyStatus.Corrupt => "授权记录损坏，拒绝执行真实电源，请检查 unattended.json。",
                UnattendedPolicyStatus.Invalid => "授权记录非法，拒绝执行真实电源。",
                UnattendedPolicyStatus.UnsupportedVersion => "授权记录版本不受支持，拒绝执行真实电源。",
                UnattendedPolicyStatus.Unavailable => "授权记录不可用，拒绝执行真实电源。",
                _ => string.IsNullOrEmpty(decision.Reason) ? "无人值守未获授权。" : decision.Reason
            };
        }

        var parts = new List<string>();
        if (policy.AuthorizationVersion > 0)
        {
            parts.Add("授权版本 V" + policy.AuthorizationVersion);
        }

        if (policy.AuthorizedAction != PowerAction.Unknown)
        {
            parts.Add("授权动作 " + UiTextMapper.Map(policy.AuthorizedAction));
        }

        if (policy.AuthorizedAtUtc != default)
        {
            parts.Add("授权时间 " + policy.AuthorizedAtUtc.ToString("yyyy-MM-dd HH:mm") + " UTC");
        }

        if (!string.IsNullOrEmpty(policy.TriggerReason))
        {
            parts.Add("触发原因 " + policy.TriggerReason);
        }

        if (policy.ExpiresAtUtc is { } expires)
        {
            parts.Add("有效期至 " + expires.ToString("yyyy-MM-dd HH:mm") + " UTC");
        }

        if (policy.RevokedAtUtc is { } revoked)
        {
            parts.Add("撤销时间 " + revoked.ToString("yyyy-MM-dd HH:mm") + " UTC");
        }

        return parts.Count == 0 ? decision.Reason : string.Join(" · ", parts);
    }

    private static string MapUnattendedEnableError(UnattendedEnableStatus status, string? error) => status switch
    {
        UnattendedEnableStatus.MissingSecondConfirmation => "缺少本地二次确认",
        UnattendedEnableStatus.InvalidAction => "授权动作无效",
        UnattendedEnableStatus.InvalidRequest => "授权请求无效",
        UnattendedEnableStatus.IoFailure => string.IsNullOrEmpty(error) ? "授权写入失败" : error,
        _ => string.IsNullOrEmpty(error) ? "启用失败" : error
    };

    // ---- 关闭应用设置（S18-D1） ----

    public ObservableCollection<CloseAppsTargetRow> CloseAppsTargets { get; } = [];

    private string _closeAppsTargetInputText = string.Empty;

    public string CloseAppsTargetInputText
    {
        get => _closeAppsTargetInputText;
        set => SetProperty(ref _closeAppsTargetInputText, value);
    }

    private string _closeAppsStatusText = string.Empty;

    public string CloseAppsStatusText
    {
        get => _closeAppsStatusText;
        private set
        {
            if (SetProperty(ref _closeAppsStatusText, value))
            {
                OnPropertyChanged(nameof(HasCloseAppsStatus));
            }
        }
    }

    public bool HasCloseAppsStatus => !string.IsNullOrEmpty(_closeAppsStatusText);

    private string _closeAppsErrorText = string.Empty;

    public string CloseAppsErrorText
    {
        get => _closeAppsErrorText;
        private set
        {
            if (SetProperty(ref _closeAppsErrorText, value))
            {
                OnPropertyChanged(nameof(HasCloseAppsError));
            }
        }
    }

    public bool HasCloseAppsError => !string.IsNullOrEmpty(_closeAppsErrorText);

    /// <summary>从已加载配置刷新 CloseApps 目标列表（每次配置加载后调用；配置不可用时清空）。</summary>
    private void RefreshCloseAppsTargets()
    {
        CloseAppsTargets.Clear();
        if (_loadedConfig?.CloseApps?.Targets is { } targets)
        {
            foreach (var target in targets)
            {
                if (target is null)
                {
                    continue;
                }

                var hasPath = !string.IsNullOrWhiteSpace(target.ExecutablePath);
                CloseAppsTargets.Add(new CloseAppsTargetRow(
                    hasPath ? Path.GetFileName(target.ExecutablePath!.Trim()) : $"pid:{target.ProcessId}",
                    hasPath ? target.ExecutablePath!.Trim() : null,
                    target.ProcessId,
                    target.GracefulTimeoutSeconds,
                    target.ForceKillAllowed,
                    ConfirmCloseAppsForceKill));
            }
        }

        CloseAppsStatusText = string.Empty;
        CloseAppsErrorText = string.Empty;
    }

    private void AddCloseAppsTarget()
    {
        var text = (CloseAppsTargetInputText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            CloseAppsErrorText = "请输入可执行文件完整路径或进程 ID";
            return;
        }

        // 稳定标识：纯正整数按进程 ID，否则按可执行文件完整路径（二者取其一）。
        if (int.TryParse(text, out var pid) && pid > 0)
        {
            if (CloseAppsTargets.Any(row => row.ProcessId == pid))
            {
                CloseAppsErrorText = "该目标已存在";
                return;
            }

            CloseAppsTargets.Add(new CloseAppsTargetRow(
                $"pid:{pid}", null, pid, null, false, ConfirmCloseAppsForceKill));
        }
        else
        {
            if (CloseAppsTargets.Any(row => string.Equals(row.ExecutablePath, text, StringComparison.OrdinalIgnoreCase)))
            {
                CloseAppsErrorText = "该目标已存在";
                return;
            }

            CloseAppsTargets.Add(new CloseAppsTargetRow(
                Path.GetFileName(text), text, null, null, false, ConfirmCloseAppsForceKill));
        }

        CloseAppsTargetInputText = string.Empty;
        CloseAppsErrorText = string.Empty;
        CloseAppsStatusText = string.Empty;
    }

    private async Task ExecuteSaveCloseAppsAsync()
    {
        if (_loadedConfig is null)
        {
            CloseAppsErrorText = "配置尚未加载，无法保存";
            return;
        }

        var targets = new List<CloseAppsTargetConfig>(CloseAppsTargets.Count);
        foreach (var row in CloseAppsTargets)
        {
            // 稳定标识恰取其一：路径或 PID（都缺失则跳过，交由校验器 fail-closed）。
            if (!string.IsNullOrWhiteSpace(row.ExecutablePath) || row.ProcessId is > 0)
            {
                targets.Add(new CloseAppsTargetConfig
                {
                    ExecutablePath = row.ExecutablePath,
                    ProcessId = row.ProcessId,
                    ForceKillAllowed = row.ForceKillAllowed,
                    GracefulTimeoutSeconds = row.GracefulTimeoutSeconds
                });
            }
        }

        var closeApps = new CloseAppsConfig
        {
            GracefulTimeoutSeconds = _loadedConfig.CloseApps?.GracefulTimeoutSeconds ?? 30,
            Targets = targets.ToArray()
        };

        var updated = _loadedConfig with { CloseApps = closeApps };

        try
        {
            var save = await _configurationService.SaveAsync(updated, CancellationToken.None);
            if (save.Succeeded)
            {
                CloseAppsErrorText = string.Empty;
                AppendActivity("已保存关闭应用设置");
                TryLog(logger => logger.Info("CloseAppsConfigSaved", "关闭应用配置已保存。"));
                await RefreshConfigurationAsync();
                // 刷新会清空状态文本，刷新后再写入成功提示。
                CloseAppsStatusText = "关闭应用设置已保存";
            }
            else
            {
                CloseAppsErrorText = "保存失败：" + string.Join("；", save.Errors);
                CloseAppsStatusText = string.Empty;
                TryLog(logger => logger.Warning("CloseAppsConfigSaveFailed", "关闭应用配置保存失败。"));
            }
        }
        catch (Exception exception)
        {
            CloseAppsErrorText = "保存异常：" + exception.Message;
            CloseAppsStatusText = string.Empty;
            TryLog(logger => logger.Warning("CloseAppsConfigSaveFailed", "关闭应用配置保存异常。"));
        }
    }

    private bool ConfirmCloseAppsForceKill()
    {
        if (_closeAppsForceKillConfirmation is not null)
        {
            return _closeAppsForceKillConfirmation();
        }

        return System.Windows.MessageBox.Show(
            "强杀将立即终止该进程，未保存的工作可能丢失。\n请确认你明确知道该进程可被强制结束。",
            "启用强杀（关闭应用）",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

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
        await RefreshUnattendedAsync();
        RefreshRecoveryNotice();
        RefreshWolAndRtcSections();
    }

    /// <summary>启动时刷新设置页分区（WoL/RTC/任务同步/远程控制；分区内部捕获错误，绝不抛出）。</summary>
    private void RefreshWolAndRtcSections()
    {
        if (_wolTargetsSection is not null)
        {
            _ = _wolTargetsSection.RefreshAsync(CancellationToken.None);
        }

        if (_rtcStatusSection is not null)
        {
            _ = _rtcStatusSection.RefreshAsync(CancellationToken.None);
        }

        if (_taskSyncSection is not null)
        {
            _ = _taskSyncSection.RefreshAsync(CancellationToken.None);
        }

        if (_remoteSection is not null)
        {
            _ = _remoteSection.RefreshAsync(CancellationToken.None);
        }
    }

    public async Task RefreshConfigurationAsync()
    {
        try
        {
            var load = await _configurationService.LoadAsync(CancellationToken.None);
            if (load.Status == ConfigurationLoadStatus.Success && load.Config is not null && load.Config.SchemaVersion == 1)
            {
                var config = load.Config;
                _loadedConfig = config;
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
                _loadedConfig = null;
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
            _loadedConfig = null;
            _configUsable = false;
            ConfigStatusText = "配置不可用：" + exception.Message;
            HeaderModeText = "配置不可用";
            HeaderModeHint = "配置加载异常，请检查设置";
            TryLog(logger => logger.Warning("ConfigurationUnavailable", "配置加载异常。"));
        }

        RefreshCloseAppsTargets();
        await RefreshUnattendedTaskAvailabilityAsync();
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
            CountdownSourceText = "—";
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
        CurrentStateText = UiTextMapper.Map(instance);
        CountdownSourceText = instance.IsIdleTriggered ? "空闲触发" : "定时排程";
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

        if (!TryValidateRuleFields(out var ruleError))
        {
            reason = ruleError;
            return false;
        }

        // S21：WoL 任务必须是时钟驱动模式（空闲触发不适用于唤醒他机）。
        if (SelectedAction == PowerAction.WakeOnLan && SelectedMode == TimeMode.Idle)
        {
            reason = "唤醒他机不支持空闲触发模式";
            return false;
        }

        // S21：WoL 任务必须显式选择目标机器（仅向用户配置的局域网目标发送）。
        if (SelectedAction == PowerAction.WakeOnLan
            && (SelectedWolTargetId is not { } wolTargetId || wolTargetId == Guid.Empty))
        {
            reason = "请选择要唤醒的目标机器";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryBuildTimeInput(out TimeSpan? duration, out TimeOnly? target, out string error)
    {
        duration = null;
        target = null;

        if (SelectedMode == TimeMode.Idle)
        {
            // 空闲触发无目标时刻/时长，阈值由 IdleThresholdIndex 独立承载。
            error = string.Empty;
            return true;
        }

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

    /// <summary>校验 S14 复杂排程的附加字段（工作日/一次性日期/节假日），无副作用。</summary>
    public bool TryValidateRuleFields(out string error)
    {
        if (SelectedMode == TimeMode.Weekdays && GetSelectedWeekdays().Count == 0)
        {
            error = "请至少选择一个工作日";
            return false;
        }

        if (SelectedMode == TimeMode.OneTime && OneTimeDate is null)
        {
            error = "请选择一次性执行的日期";
            return false;
        }

        if (!TryParseHolidayDates(out _, out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryBuildDefinition(out TaskDefinition definition, out string error)
    {
        definition = null!;

        if (SelectedAction is not (PowerAction.Shutdown or PowerAction.Restart or PowerAction.Sleep or PowerAction.Hibernate or PowerAction.WakeOnLan))
        {
            error = "请选择有效的任务类型";
            return false;
        }

        // S21：WoL 任务必须显式选择目标机器；空闲触发不适用于唤醒他机。
        if (SelectedAction == PowerAction.WakeOnLan)
        {
            if (SelectedMode == TimeMode.Idle)
            {
                error = "唤醒他机不支持空闲触发模式";
                return false;
            }

            if (SelectedWolTargetId is not { } wolTargetId || wolTargetId == Guid.Empty)
            {
                error = "请选择要唤醒的目标机器";
                return false;
            }
        }

        if (!TryBuildTimeInput(out var duration, out var target, out error))
        {
            return false;
        }

        if (!TryValidateRuleFields(out error))
        {
            return false;
        }

        var kind = SelectedMode switch
        {
            TimeMode.Countdown => TaskKind.Countdown,
            TimeMode.TodayAt => TaskKind.TodayAt,
            TimeMode.DailyAt => TaskKind.DailyAt,
            TimeMode.Weekdays => TaskKind.Weekdays,
            TimeMode.NextWorkday => TaskKind.NextWorkday,
            TimeMode.NthWorkdayOfMonth => TaskKind.NthWorkdayOfMonth,
            TimeMode.Idle => TaskKind.Idle,
            _ => TaskKind.OneTime
        };

        // 真实电源模式：创建真实任务前必须获得用户明确人工确认（双闸门之二）。
        // S20-D1：任务级「使用无人值守」——选中且授权有效时跳过人工确认，创建
        // RealPowerConfirmed=false 的任务，由调度器在倒计时边界做无人值守等效确认
        // 裁决；未选中维持现有人工确认路径（RealPowerConfirmed=true）。
        var realPowerConfirmed = false;
        var useUnattended = false;
        // S21：WoL 不是电源动作（不经双闸门），跳过真实电源人工确认路径。
        if (_configRealPowerEnabled && SelectedAction != PowerAction.WakeOnLan)
        {
            if (UseUnattended)
            {
                if (!IsUnattendedTaskOptionAvailable)
                {
                    error = "无人值守授权无效或与所选动作不匹配，无法以无人值守方式创建任务";
                    return false;
                }

                useUnattended = true;
            }
            else
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
        }

        TryParseHolidayDates(out var holidays, out _);

        definition = new TaskDefinition
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Action = SelectedAction,
            CountdownDuration = kind == TaskKind.Countdown ? duration : null,
            TargetTimeOfDay = kind is TaskKind.Countdown or TaskKind.OneTime ? null : target,
            Weekdays = kind == TaskKind.Weekdays ? GetSelectedWeekdays() : null,
            NthWorkday = kind == TaskKind.NthWorkdayOfMonth ? NthWorkdayIndex + 1 : null,
            OneTimeDateTime = kind == TaskKind.OneTime ? BuildOneTimeDateTime(target!.Value) : null,
            HolidayDates = kind == TaskKind.Countdown ? null : holidays,
            IdleThresholdSeconds = kind == TaskKind.Idle ? IdleThresholdSecondsForIndex(IdleThresholdIndex) : null,
            WarningSeconds = GetWarningSeconds(),
            CreatedAt = _clock.UtcNow,
            RealPowerConfirmed = realPowerConfirmed,
            UseUnattended = useUnattended,
            // S21：WoL 任务携带目标机器 id（仅用户显式配置的局域网目标）；电源动作不携带。
            TargetMachineId = SelectedAction == PowerAction.WakeOnLan ? SelectedWolTargetId : null
        };

        error = string.Empty;
        return true;
    }

    private IReadOnlyList<DayOfWeek> GetSelectedWeekdays()
    {
        var days = new List<DayOfWeek>(5);
        if (WeekdayMonday)
        {
            days.Add(DayOfWeek.Monday);
        }

        if (WeekdayTuesday)
        {
            days.Add(DayOfWeek.Tuesday);
        }

        if (WeekdayWednesday)
        {
            days.Add(DayOfWeek.Wednesday);
        }

        if (WeekdayThursday)
        {
            days.Add(DayOfWeek.Thursday);
        }

        if (WeekdayFriday)
        {
            days.Add(DayOfWeek.Friday);
        }

        return days;
    }

    private DateTime BuildOneTimeDateTime(TimeOnly target)
        => OneTimeDate!.Value.Date + target.ToTimeSpan();

    private bool TryParseHolidayDates(out IReadOnlyList<DateOnly>? holidays, out string error)
    {
        holidays = null;
        var text = (HolidayDatesText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = string.Empty;
            return true;
        }

        var lines = text.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<DateOnly>();
        foreach (var line in lines)
        {
            if (!DateOnly.TryParseExact(line, "yyyy-MM-dd", out var date))
            {
                error = $"例外日期「{line}」格式无效，请使用 yyyy-MM-dd";
                return false;
            }

            result.Add(date);
        }

        holidays = result;
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

        var submitted = await SubmitCommandAsync(new CreateTaskCommand(definition), "创建任务");
        if (submitted)
        {
            await PersistDefinitionAsync(definition);
        }
    }

    /// <summary>
    /// 将已成功创建的任务定义持久化到 tasks.json（S14 checkpoint 4 的"保存/恢复"）。
    /// 引擎只做内存登记与运行态写入；定义持久化由应用层负责，保证重启后周期规则仍可改期。
    /// 损坏/非法/版本不支持的 tasks.json 绝不覆盖，仅记录告警。
    /// </summary>
    private async Task PersistDefinitionAsync(TaskDefinition definition)
    {
        try
        {
            var load = await _configurationService.LoadTasksAsync(CancellationToken.None);
            List<TaskDefinition> tasks;
            if (load.Status == TasksLoadStatus.Success && load.Document is not null)
            {
                tasks = load.Document.Tasks.ToList();
            }
            else if (load.Status == TasksLoadStatus.NotFound)
            {
                tasks = [];
            }
            else
            {
                TryLog(logger => logger.Warning(
                    "TaskDefinitionPersistSkipped",
                    "任务定义未持久化：tasks.json 状态为 " + load.Status + "。"));
                return;
            }

            tasks.RemoveAll(task => task.Id == definition.Id);
            tasks.Add(definition);

            var save = await _configurationService.SaveTasksAsync(
                new TasksDocument { Tasks = tasks },
                CancellationToken.None);

            if (save.Succeeded)
            {
                AppendActivity("任务规则已保存");
            }
            else
            {
                TryLog(logger => logger.Warning(
                    "TaskDefinitionPersistFailed",
                    "任务定义保存失败：" + string.Join("；", save.Errors)));
            }
        }
        catch (Exception exception)
        {
            TryLog(logger => logger.Warning(
                "TaskDefinitionPersistFailed",
                "任务定义持久化异常：" + exception.Message));
        }
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
                UiTextMapper.Map(instance),
                localFire.ToString("yyyy-MM-dd HH:mm:ss"),
                remaining > TimeSpan.Zero ? remaining.ToString(@"hh\:mm\:ss") : "已到期",
                localWarning?.ToString("HH:mm:ss") ?? "无",
                instance.IsIdleTriggered ? "空闲" : "定时",
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

    private async Task<bool> SubmitCommandAsync(SchedulerCommand command, string displayName)
    {
        if (_isSubmitting)
        {
            return false;
        }

        LogCommandRequested(command);
        _isSubmitting = true;
        RaiseAllCommands();
        var succeeded = false;

        try
        {
            var result = await _engine.SubmitAsync(command, CancellationToken.None);
            succeeded = result.Succeeded;
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

        return succeeded;
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
