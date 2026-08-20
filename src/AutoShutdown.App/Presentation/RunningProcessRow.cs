using AutoShutdown.App.Infrastructure.ProcessSelection;
using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Presentation;

/// <summary>
/// 进程选择窗口的单行（S-CLOSEUI1）。承载只读进程信息与安全资格判定结果；
/// 不可选行标灰并显示原因（含「已添加」），选择框绑定 <see cref="IsSelected"/>（不可选时禁用）。
/// 搜索匹配进程名/路径/窗口标题/产品名称/公司名称。
/// </summary>
public sealed class RunningProcessRow : ObservableObject
{
    private bool _isSelected;

    public RunningProcessRow(
        RunningProcessInfo info,
        ProcessSelectionDecision decision,
        string? normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(decision);

        Info = info;
        Selectable = decision.Selectable;
        UnselectableReason = decision.Reason;
        NormalizedPath = normalizedPath;
    }

    public RunningProcessInfo Info { get; }

    public bool Selectable { get; }

    public string? UnselectableReason { get; }

    /// <summary>规范化后的完整绝对路径（可空：路径不可读时无）。</summary>
    public string? NormalizedPath { get; }

    public string ProcessNameDisplay => string.IsNullOrWhiteSpace(Info.ProcessName) ? "(未知)" : Info.ProcessName;

    public string PidDisplay => Info.ProcessId.ToString();

    public string PathDisplay => string.IsNullOrWhiteSpace(Info.ExecutablePath) ? "(不可读)" : Info.ExecutablePath;

    public string WindowTitleDisplay => string.IsNullOrWhiteSpace(Info.WindowTitle) ? string.Empty : Info.WindowTitle;

    public string ProductDisplay => string.IsNullOrWhiteSpace(Info.ProductName) ? string.Empty : Info.ProductName;

    public string CompanyDisplay => string.IsNullOrWhiteSpace(Info.CompanyName) ? string.Empty : Info.CompanyName;

    /// <summary>状态/不可选原因列：可选用「可添加」，否则用原因。</summary>
    public string StatusText => Selectable ? "可添加" : (UnselectableReason ?? "不可选择");

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>搜索是否命中（大小写不敏感子串；搜索框为空时恒为 true）。</summary>
    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var needle = search.Trim();
        return Contains(Info.ProcessName, needle)
            || Contains(Info.ExecutablePath, needle)
            || Contains(Info.WindowTitle, needle)
            || Contains(Info.ProductName, needle)
            || Contains(Info.CompanyName, needle)
            || Contains(ExecutablePathKey.DisplayNormalized(Info.ExecutablePath), needle);
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null
           && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
