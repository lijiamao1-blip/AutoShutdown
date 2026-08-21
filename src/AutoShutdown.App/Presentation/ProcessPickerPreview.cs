using AutoShutdown.App.Infrastructure.ProcessSelection;

namespace AutoShutdown.App.Presentation;

/// <summary>确定前路径摘要的单条项目（S-CLOSEUI1-D1）。仅用于展示，绝不参与匹配。</summary>
public sealed record ProcessPickerPreviewItem
{
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>当前 PID（仅本次确认展示，绝不保存为长期目标）。</summary>
    public int ProcessId { get; init; }

    /// <summary>完整 EXE 路径（经复核的当前路径）。</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string ProcessNameDisplay => string.IsNullOrWhiteSpace(ProcessName) ? "(未知)" : ProcessName;

    public string PathDisplay => string.IsNullOrWhiteSpace(ExecutablePath) ? "(不可读)" : ExecutablePath;
}

/// <summary>
/// 确定前路径摘要（S-CLOSEUI1-D1）。点击「确定」后、返回设置页之前先展示的复核摘要：
/// 仅包含按 PID + 启动时间 + 完整路径 + 安全资格再次复核通过且按规范化路径去重后的目标。
/// 确认摘要绝不启动、关闭或终止任何进程；只有用户点「确认添加/确认替换」后才返回结果。
/// </summary>
public sealed record ProcessPickerPreview
{
    /// <summary>重新选择单个目标模式（只允许确认一个新程序；摘要显示原路径/新路径/确认替换）。</summary>
    public bool IsReSelectMode { get; init; }

    /// <summary>复核通过且去重后的新目标（携带当前完整信息，确认后转交目标集合）。</summary>
    public IReadOnlyList<RunningProcessInfo> ConfirmedProcesses { get; init; } = [];

    /// <summary>被排除项提示（已退出/启动时间未知或不一致/路径变化/安全校验不过/路径无法规范化）。</summary>
    public string Warnings { get; init; } = string.Empty;

    /// <summary>重新选择模式下的原路径（失效目标）；仅展示。</summary>
    public string? OriginalPath { get; init; }

    /// <summary>摘要条目（程序名 / 当前 PID / 完整路径 / 窗口标题）。</summary>
    public IReadOnlyList<ProcessPickerPreviewItem> Items { get; init; } = [];

    /// <summary>最终将新增的去重后目标数量。</summary>
    public int FinalCount => Items.Count;

    // ---- 展示辅助（仅摘要对话框显示用，绝不参与匹配） ----

    public string TitleText => IsReSelectMode ? "确认替换运行中的程序目标" : "确认添加运行中的程序目标";

    public string ConfirmButtonText => IsReSelectMode ? "确认替换" : "确认添加";

    public bool ShowOriginalPath => IsReSelectMode && !string.IsNullOrWhiteSpace(OriginalPath);

    public string NewPathText => Items.Count > 0 ? Items[0].PathDisplay : string.Empty;

    public string SummaryCountText => $"最终将新增 {FinalCount} 个目标（按完整路径去重后）：";

    public string WindowTitlesText
    {
        get
        {
            var titles = Items
                .Where(item => !string.IsNullOrWhiteSpace(item.WindowTitle))
                .Select(item => item.WindowTitle.Trim())
                .ToList();
            return titles.Count == 0 ? "（无窗口标题）" : string.Join("；", titles);
        }
    }

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);
}
