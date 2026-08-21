using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using AutoShutdown.App.Infrastructure.ProcessSelection;
using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Presentation;

/// <summary>
/// 进程选择窗口视图模型（S-CLOSEUI1 + S-CLOSEUI1-D1）。只读：枚举/搜索/勾选/确定/取消全部不触碰
/// 任何进程关闭、终止、启动或窗口消息接口。枚举失败整表 fail-closed（空列表 + 错误状态），单行读取
/// 失败由探测层容错为该行标灰。确定时先构建复核摘要（PID + 启动时间 + 路径 + 安全资格再次复核，
/// 按规范化路径去重），用户确认后才返回结果；重新选择模式只允许确认一个新程序，空确认/多选/全部
/// 复核失败一律停留窗口且原目标保持不变。
/// </summary>
public sealed class ProcessPickerViewModel : ObservableObject
{
    private readonly IProcessInfoProvider _probe;
    private readonly ProcessSelectionContext _context;
    private readonly bool _isReSelectMode;
    private readonly string? _reselectOriginalPath;
    private readonly List<RunningProcessRow> _allRows = [];
    private readonly ObservableCollection<RunningProcessRow> _rows = [];
    private string _searchText = string.Empty;
    private string _statusText = "尚未读取进程信息";
    private string _warningText = string.Empty;
    private bool _isBusy;
    private bool _hasError;
    private bool _showWindowedOnly = true;
    private bool _showSelectableOnly = true;

    public ProcessPickerViewModel(
        IProcessInfoProvider processInfoProvider,
        ProcessSelectionContext context,
        bool isReSelectMode = false,
        string? reselectOriginalPath = null)
    {
        ArgumentNullException.ThrowIfNull(processInfoProvider);
        ArgumentNullException.ThrowIfNull(context);

        _probe = processInfoProvider;
        _context = context;
        _isReSelectMode = isReSelectMode;
        _reselectOriginalPath = reselectOriginalPath;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        SelectAllVisibleCommand = new RelayCommand(_ => SelectAllVisible(), () => !_isReSelectMode);
        ClearCurrentCommand = new RelayCommand(_ => ClearCurrent());
        ClearAllCommand = new RelayCommand(_ => ClearAll());
    }

    public ObservableCollection<RunningProcessRow> Rows => _rows;

    public ICommand RefreshCommand { get; }

    /// <summary>全选当前可用项：只勾选当前搜索/筛选结果中可选的可见行。重新选择模式禁用。</summary>
    public ICommand SelectAllVisibleCommand { get; }

    /// <summary>取消当前选择：只取消当前搜索/筛选结果中可见行的勾选。</summary>
    public ICommand ClearCurrentCommand { get; }

    /// <summary>取消全部选择：清除所有筛选范围内外可选行的勾选。</summary>
    public ICommand ClearAllCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsReSelectMode => _isReSelectMode;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>仅显示有窗口（默认开启）：隐藏无法确认主窗口的进程；仅影响显示，不改变勾选。</summary>
    public bool ShowWindowedOnly
    {
        get => _showWindowedOnly;
        set
        {
            if (SetProperty(ref _showWindowedOnly, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>仅显示可选项（默认开启）：隐藏所有 Selectable=false 的行；关闭后可见不可用原因。</summary>
    public bool ShowSelectableOnly
    {
        get => _showSelectableOnly;
        set
        {
            if (SetProperty(ref _showSelectableOnly, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>已选择数量（全部筛选范围内外的有效勾选总数，跨搜索/筛选保持正确）。</summary>
    public int SelectedCount => _allRows.Count(row => row.IsSelected);

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
            foreach (var row in _allRows)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
            _allRows.Clear();
            ApplyFilter();
            HasError = true;
            StatusText = "读取进程信息失败：" + exception.Message;
            OnPropertyChanged(nameof(SelectedCount));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>全选当前可用项：只勾选当前搜索/筛选结果中 Selectable=true 的可见行。</summary>
    public void SelectAllVisible()
    {
        if (_isReSelectMode)
        {
            return; // 重新选择模式不得提供批量全选（按钮已禁用，双保险）。
        }

        foreach (var row in _rows)
        {
            if (row.Selectable)
            {
                row.IsSelected = true;
            }
        }
    }

    /// <summary>取消当前选择：只取消当前搜索/筛选结果中可见行的勾选（不影响被筛选隐藏的选择）。</summary>
    public void ClearCurrent()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = false;
        }
    }

    /// <summary>取消全部选择：清除所有筛选范围内外可选行的勾选（不可选行本来无法勾选）。</summary>
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
    /// 确定：再次复核每个勾选进程「仍存在且身份未变（PID + 启动时间 + 完整路径）且安全资格仍通过」，
    /// 按规范化路径去重后构建确认摘要；返回 null 表示应停留在窗口（空选择/多选/全部复核失败，警告已写入）。
    /// </summary>
    public ProcessPickerPreview? BuildPreview()
    {
        var checkedRows = _allRows.Where(row => row.IsSelected).ToList();

        if (_isReSelectMode)
        {
            if (checkedRows.Count == 0)
            {
                WarningText = "未选择任何程序，原目标保持不变";
                return null;
            }

            if (checkedRows.Count > 1)
            {
                WarningText = "重新选择一次只能选择一个程序";
                return null;
            }
        }
        else if (checkedRows.Count == 0)
        {
            WarningText = "未选择任何程序，未添加任何目标";
            return null;
        }

        var confirmed = new List<RunningProcessInfo>();
        var items = new List<ProcessPickerPreviewItem>();
        var warnings = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in checkedRows)
        {
            var label = $"{row.ProcessNameDisplay} (PID {row.Info.ProcessId})";

            var fresh = _probe.GetById(row.Info.ProcessId);
            if (fresh is null)
            {
                warnings.Add($"{label} 已退出");
                continue;
            }

            // 进程身份复核（S-CLOSEUI1-D1）：PID 一致 + 启动时间已知且一致 + 完整路径一致；
            // 任一项未知或不一致一律 fail-closed，不得降级绕过。
            if (row.Info.StartTimeUtc == default)
            {
                warnings.Add($"{label} 启动时间未知，无法确认身份");
                continue;
            }

            if (fresh.StartTimeUtc == default)
            {
                warnings.Add($"{label} 无法确认启动时间，不降级绕过");
                continue;
            }

            if (fresh.StartTimeUtc != row.Info.StartTimeUtc)
            {
                warnings.Add($"{label} 进程身份变化（启动时间不一致）");
                continue;
            }

            if (!ExecutablePathKey.EqualsNormalized(fresh.ExecutablePath, row.Info.ExecutablePath))
            {
                warnings.Add($"{label} 路径已变化");
                continue;
            }

            var decision = ProcessSelectionGuard.Evaluate(fresh, _context);
            if (!decision.Selectable)
            {
                warnings.Add($"{label} 不再满足安全校验（{decision.Reason}）");
                continue;
            }

            var normalized = ExecutablePathKey.Normalize(fresh.ExecutablePath);
            if (normalized is null)
            {
                warnings.Add($"{label} 路径无法规范化");
                continue;
            }

            if (!seenPaths.Add(normalized))
            {
                continue; // 同一程序（同规范化完整路径）只保留一个。
            }

            confirmed.Add(fresh);
            items.Add(new ProcessPickerPreviewItem
            {
                ProcessName = fresh.ProcessName,
                ProcessId = fresh.ProcessId,
                ExecutablePath = fresh.ExecutablePath ?? string.Empty,
                WindowTitle = fresh.WindowTitle
            });
        }

        WarningText = string.Join("；", warnings);
        if (confirmed.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(WarningText))
            {
                WarningText = "所选程序均未通过复核";
            }

            return null; // 全部复核失败：停留窗口，绝不静默添加。
        }

        return new ProcessPickerPreview
        {
            IsReSelectMode = _isReSelectMode,
            ConfirmedProcesses = confirmed,
            Items = items,
            Warnings = WarningText,
            OriginalPath = _reselectOriginalPath
        };
    }

    /// <summary>摘要确认后返回最终结果（仅转交复核通过的确认列表，绝不再次触碰进程）。</summary>
    public ProcessPickerResult Commit(ProcessPickerPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var message = preview.Warnings;
        WarningText = string.Empty;
        return new ProcessPickerResult
        {
            ConfirmedProcesses = preview.ConfirmedProcesses,
            Warnings = message
        };
    }

    /// <summary>取消：清除提示，返回 null（调用方不得修改目标集合）。</summary>
    public ProcessPickerResult? Cancel()
    {
        WarningText = string.Empty;
        return null;
    }

    private void BuildRows(IReadOnlyList<RunningProcessInfo> snapshot)
    {
        var previouslySelectedPids = _allRows
            .Where(row => row.IsSelected)
            .Select(row => row.Info.ProcessId)
            .ToHashSet();
        var prior = _allRows
            .Where(row => row.IsSelected)
            .ToDictionary(row => row.Info.ProcessId);

        foreach (var row in _allRows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
        _allRows.Clear();

        foreach (var info in snapshot)
        {
            var decision = ProcessSelectionGuard.Evaluate(info, _context);
            var row = new RunningProcessRow(
                info,
                decision,
                ExecutablePathKey.Normalize(info.ExecutablePath));

            // 刷新后选择保留：仅当 PID 仍在、启动时间一致、规范化路径一致（三重身份确认）才保留；
            // 否则清除（刷新后已清除无法确认的原选择，见下方提示）。
            if (prior.TryGetValue(info.ProcessId, out var old)
                && old.Selectable
                && row.Selectable
                && old.Info.StartTimeUtc != default
                && old.Info.StartTimeUtc == info.StartTimeUtc
                && ExecutablePathKey.EqualsNormalized(old.Info.ExecutablePath, info.ExecutablePath))
            {
                row.IsSelected = true;
            }

            row.PropertyChanged += OnRowPropertyChanged;
            _allRows.Add(row);
        }

        var nowSelectedPids = _allRows
            .Where(row => row.IsSelected)
            .Select(row => row.Info.ProcessId)
            .ToHashSet();
        var dropped = previouslySelectedPids.Count(pid => !nowSelectedPids.Contains(pid));
        if (dropped > 0)
        {
            WarningText = $"刷新后已清除 {dropped} 项无法确认的原选择";
        }

        ApplyFilter();

        var selectableCount = _allRows.Count(row => row.Selectable);
        StatusText = $"共 {_allRows.Count} 个进程，{selectableCount} 个可添加";
        HasError = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void ApplyFilter()
    {
        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (!row.Matches(_searchText))
            {
                continue;
            }

            if (_showWindowedOnly && !row.HasWindow)
            {
                continue;
            }

            if (_showSelectableOnly && !row.Selectable)
            {
                continue;
            }

            _rows.Add(row);
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunningProcessRow.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
        }
    }
}
