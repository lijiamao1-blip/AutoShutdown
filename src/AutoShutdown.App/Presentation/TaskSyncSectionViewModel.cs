using System.Windows.Input;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.App.Presentation;

/// <summary>
/// Windows 任务计划程序单向同步分区（S22 CP4 设置页）。以本地 TaskCollection 为唯一事实源，
/// 只外发（创建/更新/删除/查询），绝不 inbound。关闭同步 = 清理全部自家外部计划任务（外部
/// 无本应用任务，绝不残留）；启用同步 = 立即用当前本地事实源重建。权限/IO 失败如实显示修复
/// 建议；读取 task-sync.json 严格区分 NotFound/Corrupt/Invalid/UnsupportedVersion，
/// 损坏/非法一律保持同步关闭（fail-closed，绝不静默启用）。
/// </summary>
public sealed class TaskSyncSectionViewModel : ObservableObject
{
    private readonly TaskSyncSettingsStore _settingsStore;
    private readonly TaskSyncCoordinator _coordinator;
    private readonly ITaskSchedulerAdapter _adapter;
    private readonly IClock _clock;
    private readonly Action<string>? _log;

    private bool _enabled;
    private bool _isBusy;
    private bool _isApplyingEnabled;
    private string _statusText = "已关闭";
    private string _detailText = string.Empty;
    private string _errorText = string.Empty;
    private string _lastSyncText = string.Empty;

    public TaskSyncSectionViewModel(
        TaskSyncSettingsStore settingsStore,
        TaskSyncCoordinator coordinator,
        ITaskSchedulerAdapter adapter,
        IClock clock,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(clock);

        _settingsStore = settingsStore;
        _coordinator = coordinator;
        _adapter = adapter;
        _clock = clock;
        _log = log;

        SyncNowCommand = new AsyncRelayCommand(ExecuteSyncNowAsync, () => CanOperate);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None),
            () => !_isBusy);
        _coordinator.SyncCompleted += OnSyncCompleted;
        _ = RefreshAsync(CancellationToken.None);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled || _isApplyingEnabled)
            {
                return;
            }

            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanOperate));
            _log?.Invoke("用户切换任务计划程序同步：" + (value ? "启用" : "关闭"));
            _ = ApplyEnabledAsync(value);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public string LastSyncText
    {
        get => _lastSyncText;
        private set => SetProperty(ref _lastSyncText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanOperate));
                SyncNowCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanOperate => !_isBusy;

    public AsyncRelayCommand SyncNowCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// 从 task-sync.json 恢复状态。NotFound 视为首次使用（关闭）；Corrupt/Invalid/
    /// UnsupportedVersion/IoFailure 一律保持同步关闭并上报，绝不静默启用（启用会创建
    /// 外部计划任务，必须以干净的配置为前提）。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ErrorText = string.Empty;
        try
        {
            var load = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            switch (load.Status)
            {
                case TaskSyncSettingsLoadStatus.Success:
                    _enabled = load.Document?.Enabled ?? false;
                    break;

                case TaskSyncSettingsLoadStatus.NotFound:
                    _enabled = false;
                    break;

                default:
                    _enabled = false;
                    ErrorText = "同步配置不可用（" + load.Status + "）。已保持同步关闭，绝不自动启用。";
                    break;
            }

            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(CanOperate));
            // 同步门与存储一致：启动时若存储为关闭，初始加载的防抖同步立即失效（不建外部任务）。
            _coordinator.Enabled = _enabled;
            StatusText = _enabled ? "已启用" : "已关闭";

            if (load.Status == TaskSyncSettingsLoadStatus.Success
                && load.Document?.LastSyncAtUtc is { } lastSync)
            {
                LastSyncText = "上次同步：" + TimeZoneInfo
                    .ConvertTime(lastSync, _clock.LocalTimeZone)
                    .ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
        catch (Exception exception)
        {
            ErrorText = "读取同步设置失败：" + exception.Message;
        }
    }

    private async Task ExecuteSyncNowAsync()
    {
        if (!CanOperate)
        {
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            await _coordinator.SyncNowAsync(CancellationToken.None).ConfigureAwait(true);
            _log?.Invoke("手动执行任务计划程序同步。");
        }
        catch (Exception exception)
        {
            ErrorText = "手动同步异常：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 开关副作用：启用 → 持久化并立即同步（重建外部任务）；关闭 → 先清理全部自家外部
    /// 任务（同步关闭 = 外部无本应用任务）再持久化。任一失败都回滚开关并如实报错，绝不
    /// 出现「显示已关闭但外部任务仍在触发」或「显示已启用但外部无任务」的漂移。
    /// </summary>
    private async Task ApplyEnabledAsync(bool enabled)
    {
        if (_isApplyingEnabled)
        {
            return;
        }

        _isApplyingEnabled = true;
        IsBusy = true;
        try
        {
            if (enabled)
            {
                var save = await _settingsStore.SaveAsync(
                    new TaskSyncSettingsDocument { Enabled = true },
                    CancellationToken.None).ConfigureAwait(true);
                if (!save.Succeeded)
                {
                    ErrorText = "保存同步设置失败：" + (save.Error ?? string.Empty);
                    RevertEnabled(enabled);
                    return;
                }

                _coordinator.Enabled = true;
                StatusText = "已启用（正在同步…）";
                await _coordinator.SyncNowAsync(CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                // 先关同步门：此后任何防抖/排空同步一律跳过，外部任务绝不被带回来。
                _coordinator.Enabled = false;
                // 排空在途同步：信号量保证等它结束后外部状态稳定，再清理。
                await _coordinator.SyncNowAsync(CancellationToken.None).ConfigureAwait(true);

                if (!await ClearExternalAsync().ConfigureAwait(true))
                {
                    ErrorText = "无法清除已有计划任务，未关闭同步（fail-closed）。请以管理员身份运行后重试。";
                    RevertEnabled(enabled);
                    return;
                }

                var save = await _settingsStore.SaveAsync(
                    new TaskSyncSettingsDocument { Enabled = false },
                    CancellationToken.None).ConfigureAwait(true);
                if (!save.Succeeded)
                {
                    ErrorText = "保存同步设置失败：" + (save.Error ?? string.Empty);
                    RevertEnabled(enabled);
                    return;
                }

                StatusText = "已关闭（外部计划任务已全部清理）";
                DetailText = string.Empty;
                LastSyncText = string.Empty;
                _log?.Invoke("任务计划程序同步已关闭，外部任务已清理。");
            }
        }
        catch (Exception exception)
        {
            ErrorText = "切换同步失败：" + exception.Message;
            RevertEnabled(enabled);
        }
        finally
        {
            IsBusy = false;
            _isApplyingEnabled = false;
        }
    }

    /// <summary>清理全部自家外部任务（三重条件由适配器保证，绝不触碰他应用任务）。</summary>
    private async Task<bool> ClearExternalAsync()
    {
        var query = await _adapter.QueryOwnedAsync(CancellationToken.None).ConfigureAwait(true);
        if (query.Status != ExternalTaskQueryStatus.Success)
        {
            ErrorText = query.Status == ExternalTaskQueryStatus.PermissionDenied
                ? "无法访问任务计划程序（权限不足）。修复建议：以管理员身份运行本程序一次。"
                : "无法访问任务计划程序：" + (query.Message ?? query.Status.ToString());
            return false;
        }

        var ok = true;
        foreach (var task in query.Tasks ?? [])
        {
            var deleted = await _adapter.DeleteAsync(task.Name, CancellationToken.None).ConfigureAwait(true);
            if (!deleted.Succeeded)
            {
                ok = false;
            }
        }

        return ok;
    }

    private void OnSyncCompleted(object? sender, TaskSyncReport report)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyReport(report);
        }
        else
        {
            dispatcher.InvokeAsync(() => ApplyReport(report));
        }
    }

    private void ApplyReport(TaskSyncReport report)
    {
        IsBusy = false;
        if (!report.Succeeded)
        {
            StatusText = "同步失败";
            DetailText = string.Empty;
            ErrorText = BuildFailureText(report);
            return;
        }

        ErrorText = string.Empty;
        StatusText = $"同步完成：新增 {report.Created}，更新 {report.Updated}，删除 {report.Deleted}，未变 {report.Unchanged}";
        DetailText = report.SkippedForeign > 0
            ? $"跳过非本应用外部任务 {report.SkippedForeign}（只清理自家任务）"
            : string.Empty;

        var utcNow = _clock.UtcNow;
        LastSyncText = "上次同步：" + TimeZoneInfo
            .ConvertTime(utcNow, _clock.LocalTimeZone)
            .ToString("yyyy-MM-dd HH:mm:ss");
        _log?.Invoke($"任务计划程序同步完成（+{report.Created}/~{report.Updated}/-{report.Deleted}/={report.Unchanged}）。");

        // 审计时间：尽力写入，失败不影响本次同步结果展示。
        _ = _settingsStore.SaveAsync(
            new TaskSyncSettingsDocument { Enabled = _enabled, LastSyncAtUtc = utcNow },
            CancellationToken.None);
    }

    private static string BuildFailureText(TaskSyncReport report)
    {
        var lines = new List<string>();
        switch (report.QueryStatus)
        {
            case ExternalTaskQueryStatus.PermissionDenied:
                lines.Add("无法访问任务计划程序（权限不足）。修复建议：以管理员身份运行本程序一次；若仍失败，请在「任务计划程序」中检查 AutoShutdown V2 目录权限。");
                break;
            case ExternalTaskQueryStatus.IoFailure:
                lines.Add("访问任务计划程序失败（IO）：" + (report.QueryFailureMessage ?? string.Empty));
                break;
        }

        foreach (var entry in report.Entries
                     .Where(entry => entry.Status == TaskSyncEntryStatus.Failed)
                     .Take(3))
        {
            lines.Add(entry.ExternalTaskName + "： " + (entry.Message ?? string.Empty));
        }

        return string.Join("\n", lines);
    }

    private void RevertEnabled(bool attemptedValue)
    {
        _enabled = !attemptedValue;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(CanOperate));
    }
}
