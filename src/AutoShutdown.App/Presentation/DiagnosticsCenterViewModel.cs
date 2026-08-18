using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Input;
using AutoShutdown.App.Infrastructure.Diagnostics;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.App.Presentation;

/// <summary>单条日志行（解析后；FullText 为原始整行用于复制）。</summary>
public sealed record LogEntryRow(string Time, string Level, string Event, string Message, string FullText);

/// <summary>诊断中心所需的实时快照（由组合根从 MainWindowViewModel 及其分区汇总）。</summary>
public sealed record DiagnosticsLiveSummary(
    string VersionText,
    string BuildCommitText,
    string SigningStatusText,
    string HeaderModeText,
    string ConfigStatusText,
    bool ConfigUsable,
    string SchedulerStatusText,
    string DataRoot,
    string LogDirectory,
    IReadOnlyList<DiagnosticsTaskRow> Tasks,
    IReadOnlyList<DiagnosticsWolTargetRow> WolTargets,
    bool TaskSyncHealthy,
    string TaskSyncStatusText,
    string TaskSyncDetailText,
    bool RemoteEnabled,
    string RemoteListenAddress,
    int? RemoteListenPort,
    bool RemoteRequireTls,
    string RemotePinPresenceText,
    IReadOnlyList<DiagnosticsPairedDevice> RemoteDevices);

/// <summary>
/// 日志与诊断中心（S-UI1）：日志目录/文件/条目查看、刷新、错误筛选、搜索、复制选中记录、
/// 打开日志目录、复制诊断摘要、只读安全自检、导出脱敏诊断包、截图当前窗口。
///
/// 全部能力均为只读或用户显式点击触发；安全自检不自动修复、导出与截图绝不自动上传/外传。
/// </summary>
public sealed class DiagnosticsCenterViewModel : ObservableObject
{
    private const string LogFilePattern = "autoshutdown-*.log";

    private readonly IApplicationLogger _logger;
    private readonly IShellOpenService _shell;
    private readonly SafetySelfCheckRunner _selfCheckRunner;
    private readonly DiagnosticsPackageExporter _exporter;
    private readonly IWindowScreenshotService _screenshotService;
    private readonly IClock _clock;
    private readonly Func<Window?> _windowProvider;
    private readonly Func<string> _dataRootProvider;
    private readonly Func<DiagnosticsLiveSummary> _liveSummaryProvider;
    private readonly Action<string> _clipboardWriter;

    private string _selectedLogFileName = string.Empty;
    private string _searchText = string.Empty;
    private bool _showErrorsOnly;
    private LogEntryRow? _selectedLogEntry;
    private string _selfCheckSummaryText = string.Empty;
    private bool _isSelfCheckRunning;
    private string _diagnosticsStatusText = "就绪";
    private bool _includePrivacyInfo;
    private bool _hasStatusError;

    public DiagnosticsCenterViewModel(
        IApplicationLogger logger,
        IShellOpenService shell,
        SafetySelfCheckRunner selfCheckRunner,
        DiagnosticsPackageExporter exporter,
        IWindowScreenshotService screenshotService,
        IClock clock,
        Func<Window?> windowProvider,
        Func<string> dataRootProvider,
        Func<DiagnosticsLiveSummary> liveSummaryProvider,
        Action<string>? clipboardWriter = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(selfCheckRunner);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(screenshotService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(windowProvider);
        ArgumentNullException.ThrowIfNull(dataRootProvider);
        ArgumentNullException.ThrowIfNull(liveSummaryProvider);

        _logger = logger;
        _shell = shell;
        _selfCheckRunner = selfCheckRunner;
        _exporter = exporter;
        _screenshotService = screenshotService;
        _clock = clock;
        _windowProvider = windowProvider;
        _dataRootProvider = dataRootProvider;
        _liveSummaryProvider = liveSummaryProvider;
        _clipboardWriter = clipboardWriter ?? new Action<string>(text => System.Windows.Clipboard.SetText(text));

        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        CopySelectedLogCommand = new RelayCommand(CopySelectedLog, () => SelectedLogEntry is not null);
        OpenLogDirectoryCommand = new RelayCommand(() => _shell.OpenDirectory(LogDirectory));
        CopyDiagnosticSummaryCommand = new RelayCommand(CopyDiagnosticSummary);
        RunSelfCheckCommand = new AsyncRelayCommand(ExecuteRunSelfCheckAsync, () => !_isSelfCheckRunning);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExecuteExportDiagnosticsAsync);
        ScreenshotCommand = new RelayCommand(ExecuteScreenshot);
    }

    public string LogDirectory => _logger.LogDirectory;

    public ObservableCollection<string> LogFileNames { get; } = [];

    public ObservableCollection<LogEntryRow> LogEntries { get; } = [];

    public ObservableCollection<SelfCheckItem> SelfCheckItems { get; } = [];

    public IReadOnlyList<string> LevelFilterOptions { get; } = ["全部", "错误", "警告", "信息", "调试"];

    private int _levelFilterIndex;

    public int LevelFilterIndex
    {
        get => _levelFilterIndex;
        set
        {
            if (SetProperty(ref _levelFilterIndex, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedLogFileName
    {
        get => _selectedLogFileName;
        set
        {
            if (SetProperty(ref _selectedLogFileName, value))
            {
                LoadLogFile(value);
            }
        }
    }

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

    public bool ShowErrorsOnly
    {
        get => _showErrorsOnly;
        set
        {
            if (SetProperty(ref _showErrorsOnly, value))
            {
                ApplyFilter();
            }
        }
    }

    public LogEntryRow? SelectedLogEntry
    {
        get => _selectedLogEntry;
        set => SetProperty(ref _selectedLogEntry, value);
    }

    public string SelfCheckSummaryText
    {
        get => _selfCheckSummaryText;
        private set => SetProperty(ref _selfCheckSummaryText, value);
    }

    public bool IsSelfCheckRunning
    {
        get => _isSelfCheckRunning;
        private set
        {
            if (SetProperty(ref _isSelfCheckRunning, value))
            {
                RunSelfCheckCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DiagnosticsStatusText
    {
        get => _diagnosticsStatusText;
        private set
        {
            if (SetProperty(ref _diagnosticsStatusText, value))
            {
                _hasStatusError = value.Contains("失败", StringComparison.Ordinal);
                OnPropertyChanged(nameof(HasStatusError));
            }
        }
    }

    public bool HasStatusError => _hasStatusError;

    /// <summary>导出时是否包含隐私信息（路径/IP/MAC/机器名）。默认 false（脱敏）。</summary>
    public bool IncludePrivacyInfo
    {
        get => _includePrivacyInfo;
        set => SetProperty(ref _includePrivacyInfo, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CopySelectedLogCommand { get; }

    public RelayCommand OpenLogDirectoryCommand { get; }

    public RelayCommand CopyDiagnosticSummaryCommand { get; }

    public AsyncRelayCommand RunSelfCheckCommand { get; }

    public AsyncRelayCommand ExportDiagnosticsCommand { get; }

    public RelayCommand ScreenshotCommand { get; }

    // ---- 刷新日志 ----

    private Task ExecuteRefreshAsync()
    {
        RefreshLogFiles();
        DiagnosticsStatusText = $"已刷新（{_clock.UtcNow.ToLocalTime():HH:mm:ss}）";
        return Task.CompletedTask;
    }

    public void RefreshLogFiles()
    {
        var previous = SelectedLogFileName;
        LogFileNames.Clear();

        var files = EnumerateLogFiles();
        foreach (var file in files)
        {
            LogFileNames.Add(Path.GetFileName(file));
        }

        if (LogFileNames.Count > 0)
        {
            SelectedLogFileName = string.IsNullOrEmpty(previous) || !LogFileNames.Contains(previous)
                ? LogFileNames[0]
                : previous;
        }
        else
        {
            SelectedLogFileName = string.Empty;
            LogEntries.Clear();
        }
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(LogDirectory, LogFilePattern)
                .OrderByDescending(File.GetLastWriteTime);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private void LoadLogFile(string fileName)
    {
        LogEntries.Clear();
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        var path = Path.Combine(LogDirectory, fileName);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using var reader = new StreamReader(path, Encoding.UTF8);
            string? line;
            var all = new List<LogEntryRow>();
            while ((line = reader.ReadLine()) is not null)
            {
                all.Add(ParseLogLine(line));
            }

            _allEntries = all;
            ApplyFilter();
        }
        catch (Exception)
        {
            // 日志读取失败不影响页面；留空列表。
        }
    }

    private List<LogEntryRow> _allEntries = [];

    private static LogEntryRow ParseLogLine(string line)
    {
        // 格式：yyyy-MM-dd'T'HH:mm:ss.fffzzz [Level] Event Message
        const int timeLength = 29;
        if (line.Length >= timeLength + 3 && line[timeLength] == ' ')
        {
            var time = line[..timeLength];
            var rest = line[(timeLength + 1)..];
            var levelStart = rest.IndexOf('[');
            var levelEnd = levelStart >= 0 ? rest.IndexOf(']', levelStart) : -1;
            if (levelStart >= 0 && levelEnd > levelStart)
            {
                var level = rest[(levelStart + 1)..levelEnd];
                var eventName = string.Empty;
                var message = string.Empty;
                var tail = rest[(levelEnd + 1)..].TrimStart();
                var spaceIndex = tail.IndexOf(' ');
                if (spaceIndex > 0)
                {
                    eventName = tail[..spaceIndex];
                    message = tail[(spaceIndex + 1)..];
                }
                else
                {
                    message = tail;
                }

                return new LogEntryRow(time, level, eventName, message, line);
            }
        }

        return new LogEntryRow(string.Empty, "-", string.Empty, line, line);
    }

    private void ApplyFilter()
    {
        if (_allEntries.Count == 0)
        {
            return;
        }

        var query = _searchText?.Trim() ?? string.Empty;
        var onlyErrors = ShowErrorsOnly;
        var levelFilter = LevelFilterIndex;

        var filtered = _allEntries.Where(entry =>
        {
            if (onlyErrors && !IsErrorLevel(entry.Level))
            {
                return false;
            }

            if (levelFilter == 1 && !IsErrorLevel(entry.Level))
            {
                return false;
            }

            if (levelFilter == 2 && entry.Level != "Warning")
            {
                return false;
            }

            if (levelFilter == 3 && entry.Level != "Information")
            {
                return false;
            }

            if (levelFilter == 4 && entry.Level != "Debug")
            {
                return false;
            }

            if (query.Length > 0
                && !entry.FullText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        });

        LogEntries.Clear();
        foreach (var entry in filtered)
        {
            LogEntries.Add(entry);
        }
    }

    private static bool IsErrorLevel(string level)
        => level is "Error" or "Warning";

    // ---- 复制 ----

    private void CopySelectedLog()
    {
        if (SelectedLogEntry is null)
        {
            return;
        }

        _clipboardWriter(SelectedLogEntry.FullText);
        DiagnosticsStatusText = "已复制选中日志记录";
    }

    private void CopyDiagnosticSummary()
    {
        var content = BuildExportContent();
        _clipboardWriter(_exporter.BuildSummaryText(content));
        DiagnosticsStatusText = "已复制诊断摘要";
    }

    // ---- 安全自检 ----

    private async Task ExecuteRunSelfCheckAsync()
    {
        if (IsSelfCheckRunning)
        {
            return;
        }

        IsSelfCheckRunning = true;
        try
        {
            var summary = _liveSummaryProvider();
            var snapshot = BuildSelfCheckSnapshot(summary);
            var report = await _selfCheckRunner.RunSelfCheckAsync(snapshot).ConfigureAwait(true);

            SelfCheckItems.Clear();
            foreach (var item in report.Items)
            {
                SelfCheckItems.Add(item);
            }

            SelfCheckSummaryText = report.SummaryText;
            DiagnosticsStatusText = report.AllHealthy ? "安全自检完成：全部通过（只读）" : "安全自检完成：存在待关注项（未自动修复）";
        }
        catch (Exception exception)
        {
            _logger.Error("SelfCheck", "安全自检失败", exception);
            SelfCheckSummaryText = $"安全自检执行失败：{exception.Message}";
            DiagnosticsStatusText = "安全自检执行失败";
        }
        finally
        {
            IsSelfCheckRunning = false;
        }
    }

    private SelfCheckSnapshot BuildSelfCheckSnapshot(DiagnosticsLiveSummary summary)
    {
        var wolHealth = summary.WolTargets
            .Select(WolTargetHealthBuilders.FromRow)
            .ToList();

        return new SelfCheckSnapshot(
            DataRoot: summary.DataRoot,
            LogDirectory: summary.LogDirectory,
            ConfigStatusText: summary.ConfigStatusText,
            ConfigUsable: summary.ConfigUsable,
            LocalTaskCount: summary.Tasks.Count,
            TaskSyncStatusText: summary.TaskSyncStatusText,
            TaskSyncHealthy: summary.TaskSyncHealthy,
            TaskSyncDetailText: summary.TaskSyncDetailText,
            WolTargets: wolHealth,
            RemoteEnabled: summary.RemoteEnabled,
            RemoteListenAddress: summary.RemoteListenAddress,
            RemoteListenPort: summary.RemoteListenPort,
            RemoteRequireTls: summary.RemoteRequireTls,
            RemoteDetailText: string.Empty);
    }

    // ---- 导出脱敏诊断包 ----

    private async Task ExecuteExportDiagnosticsAsync()
    {
        var content = BuildExportContent();
        var window = _windowProvider();
        if (window is null)
        {
            DiagnosticsStatusText = "导出失败：无法定位主窗口";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出脱敏诊断包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"autoshutdown-diagnostics-{_clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.zip"
        };

        if (dialog.ShowDialog(window) != true)
        {
            return;
        }

        var succeeded = await _exporter.ExportAsync(content, dialog.FileName, CancellationToken.None).ConfigureAwait(true);
        if (succeeded)
        {
            DiagnosticsStatusText = $"诊断包已导出：{dialog.FileName}";
            _shell.OpenFolderAndSelectFile(dialog.FileName);
        }
        else
        {
            DiagnosticsStatusText = "导出诊断包失败（详见日志）";
        }
    }

    private DiagnosticsExportContent BuildExportContent()
    {
        var summary = _liveSummaryProvider();
        var includePrivacy = IncludePrivacyInfo;

        return new DiagnosticsExportContent(
            AppName: AppInfo.ProductName,
            VersionText: summary.VersionText,
            BuildCommitText: summary.BuildCommitText,
            SigningStatusText: summary.SigningStatusText,
            HeaderModeText: summary.HeaderModeText,
            ConfigStatusText: summary.ConfigStatusText,
            SchedulerStatusText: summary.SchedulerStatusText,
            DataRoot: summary.DataRoot,
            LogDirectory: summary.LogDirectory,
            ConfigJsonText: ReadFileSafely(Path.Combine(summary.DataRoot, "config.json")),
            Tasks: summary.Tasks,
            WolTargets: summary.WolTargets,
            Remote: new DiagnosticsRemoteStatus(
                summary.RemoteEnabled,
                summary.RemoteListenAddress,
                summary.RemoteListenPort?.ToString() ?? string.Empty,
                summary.RemoteRequireTls,
                summary.RemotePinPresenceText,
                summary.RemoteDevices),
            SelfCheckItems: SelfCheckItems.ToList(),
            IncludePrivacyInfo: includePrivacy,
            CreatedAtUtc: _clock.UtcNow);
    }

    // ---- 截图当前窗口 ----

    private void ExecuteScreenshot()
    {
        var succeeded = _screenshotService.SaveCurrentWindowScreenshot(out var savedPath);
        DiagnosticsStatusText = succeeded
            ? $"窗口截图已保存：{savedPath}"
            : "截图已取消或保存失败";
    }

    private static string ReadFileSafely(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "（文件不存在）";
        }
        catch (Exception exception)
        {
            return $"（读取失败：{exception.GetType().Name}）";
        }
    }
}

/// <summary>WoL 目标行合法性（独立于 Core 校验，从展示行字符串重算，避免依赖额外类型）。</summary>
public static class WolTargetHealthBuilders
{
    public static WolTargetHealth FromRow(DiagnosticsWolTargetRow row)
    {
        var problems = new List<string>();
        if (!System.Net.NetworkInformation.PhysicalAddress.TryParse(row.Mac.Replace("-", ":"), out _))
        {
            problems.Add("MAC 格式非法");
        }

        if (!string.IsNullOrEmpty(row.BroadcastText)
            && (!IPAddress.TryParse(row.BroadcastText, out var broadcast)
                || broadcast.AddressFamily != AddressFamily.InterNetwork))
        {
            problems.Add("广播地址非法");
        }

        if (!string.IsNullOrEmpty(row.PortText)
            && (!int.TryParse(row.PortText, out var port) || port is < 1 or > 65535))
        {
            problems.Add("端口非法");
        }

        return new WolTargetHealth(
            row.Name,
            problems.Count == 0,
            problems.Count == 0 ? "合法" : string.Join("；", problems));
    }
}
