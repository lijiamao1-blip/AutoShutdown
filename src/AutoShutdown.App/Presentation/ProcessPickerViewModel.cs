using System.Collections.ObjectModel;
using System.Windows.Input;
using AutoShutdown.App.Infrastructure.ProcessSelection;
using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Presentation;

/// <summary>
/// 进程选择窗口视图模型（S-CLOSEUI1）。只读：枚举/搜索/勾选/确定/取消全部不触碰任何进程
/// 关闭、终止、启动或窗口消息接口。枚举失败整表 fail-closed（空列表 + 错误状态），单行读取
/// 失败由探测层容错为该行标灰。确定时再次复核进程存在且路径一致后才返回；取消不改动目标集合。
/// </summary>
public sealed class ProcessPickerViewModel : ObservableObject
{
    private readonly IProcessInfoProvider _probe;
    private readonly ProcessSelectionContext _context;
    private readonly List<RunningProcessRow> _allRows = [];
    private readonly ObservableCollection<RunningProcessRow> _rows = [];
    private string _searchText = string.Empty;
    private string _statusText = "尚未读取进程信息";
    private string _warningText = string.Empty;
    private bool _isBusy;
    private bool _hasError;

    public ProcessPickerViewModel(
        IProcessInfoProvider processInfoProvider,
        ProcessSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(processInfoProvider);
        ArgumentNullException.ThrowIfNull(context);

        _probe = processInfoProvider;
        _context = context;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        ClearAllCommand = new RelayCommand(_ => ClearAll());
    }

    public ObservableCollection<RunningProcessRow> Rows => _rows;

    public ICommand RefreshCommand { get; }

    public ICommand ClearAllCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplySearch();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    public bool HasWarnings => !string.IsNullOrEmpty(_warningText);

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>异步枚举进程并重建列表；枚举异常整表失败（fail-closed）。</summary>
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在读取进程信息…";
        HasError = false;
        try
        {
            // 后台枚举避免阻塞 UI；只读，绝不启动/关闭进程。
            var snapshot = await Task.Run(() => _probe.EnumerateProcesses()).ConfigureAwait(true);
            BuildRows(snapshot);
        }
        catch (Exception exception)
        {
            _allRows.Clear();
            ApplySearch();
            HasError = true;
            StatusText = "读取进程信息失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>全部取消：清除所有可选行的勾选（不可选行本来无法勾选）。</summary>
    public void ClearAll()
    {
        foreach (var row in _allRows)
        {
            if (row.Selectable)
            {
                row.IsSelected = false;
            }
        }
    }

    /// <summary>
    /// 确定：再次复核每个勾选进程「仍存在且路径仍与显示时一致」，并复核安全资格；复核通过者
    /// 返回，被排除者写入 <see cref="WarningText"/>。返回非 null 的 <see cref="ProcessPickerResult"/>
    /// （可能为空确认列表），供窗口决定关闭/提示。
    /// </summary>
    public ProcessPickerResult? ConfirmSelection()
    {
        var checkedRows = _allRows.Where(row => row.IsSelected).ToList();
        if (checkedRows.Count == 0)
        {
            WarningText = string.Empty;
            return new ProcessPickerResult { ConfirmedProcesses = [], Warnings = string.Empty };
        }

        var confirmed = new List<RunningProcessInfo>();
        var warnings = new List<string>();

        foreach (var row in checkedRows)
        {
            var fresh = _probe.GetById(row.Info.ProcessId);
            if (fresh is null)
            {
                warnings.Add($"{row.ProcessNameDisplay} (PID {row.Info.ProcessId}) 已退出");
                continue;
            }

            if (!ExecutablePathKey.EqualsNormalized(fresh.ExecutablePath, row.Info.ExecutablePath))
            {
                warnings.Add($"{row.ProcessNameDisplay} (PID {row.Info.ProcessId}) 路径已变化");
                continue;
            }

            // 按当前快照再次复核安全资格（fail-closed：复核不过绝不加入）。
            var decision = ProcessSelectionGuard.Evaluate(fresh, _context);
            if (!decision.Selectable)
            {
                warnings.Add($"{row.ProcessNameDisplay} (PID {row.Info.ProcessId}) 不再满足安全校验");
                continue;
            }

            confirmed.Add(fresh);
        }

        WarningText = string.Join("；", warnings);
        return new ProcessPickerResult { ConfirmedProcesses = confirmed, Warnings = WarningText };
    }

    /// <summary>取消：清除提示，返回 null（调用方不得修改目标集合）。</summary>
    public ProcessPickerResult? Cancel()
    {
        WarningText = string.Empty;
        return null;
    }

    private void BuildRows(IReadOnlyList<RunningProcessInfo> snapshot)
    {
        _allRows.Clear();
        foreach (var info in snapshot)
        {
            var decision = ProcessSelectionGuard.Evaluate(info, _context);
            _allRows.Add(new RunningProcessRow(
                info,
                decision,
                ExecutablePathKey.Normalize(info.ExecutablePath)));
        }

        ApplySearch();

        var selectableCount = _allRows.Count(row => row.Selectable);
        StatusText = $"共 {_allRows.Count} 个进程，{selectableCount} 个可添加";
        HasError = false;
    }

    private void ApplySearch()
    {
        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (row.Matches(_searchText))
            {
                _rows.Add(row);
            }
        }
    }
}
