using System.IO;
using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Infrastructure.ProcessSelection;

/// <summary>进程选择判定上下文（S-CLOSEUI1）。只读快照，由窗口/VM 在打开时构建。</summary>
public sealed record ProcessSelectionContext
{
    public int CurrentProcessId { get; init; }

    public int CurrentSessionId { get; init; }

    /// <summary>AutoShutdown 自身可执行文件全路径（用于自身识别）。</summary>
    public string? CurrentExecutablePath { get; init; }

    /// <summary>已存在于关闭目标列表中的原始路径集合（按规范化路径判「已添加」）。</summary>
    public IReadOnlyCollection<string> ExistingTargetPaths { get; init; } = [];
}

/// <summary>进程选择判定结果。Selectable=false 时 Reason 为不可选原因（供 UI 标灰显示）。</summary>
public sealed record ProcessSelectionDecision(bool Selectable, string? Reason)
{
    public static ProcessSelectionDecision Selectable_ => new(true, null);

    public static ProcessSelectionDecision NotSelectable(string reason) => new(false, reason);
}

/// <summary>
/// 关闭目标选择的只读安全资格评估（S-CLOSEUI1）。只有满足关闭执行安全校验的进程才允许选择：
/// 完整绝对 EXE 路径可读、指向现有普通文件、不是 AutoShutdown 自身/辅助进程、不是 Windows
/// 核心系统进程、不是系统服务/其他会话进程；相对路径、环境变量占位、reparse point/junction/
/// symlink 及无法确认的路径状态一律 fail-closed 标为不可用。纯函数，不触碰任何进程/文件写操作。
/// 已存在于目标列表中的路径显示「已添加」且不可重复选择。
/// </summary>
public static class ProcessSelectionGuard
{
    /// <summary>Windows 核心系统进程名（与 CloseApps 执行边界关键名一致 + 补充防御）。</summary>
    public static readonly IReadOnlySet<string> CriticalSystemProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memory compression", "csrss", "winlogon",
            "services", "lsass", "smss", "wininit", "dwm", "fontdrvhost", "audiodg",
            "svchost", "lsaiso", "wininit", "conhost", "fontdrvhost"
        };

    /// <summary>AutoShutdown 辅助进程的可执行文件基名（不含扩展名）。</summary>
    public static readonly IReadOnlySet<string> KnownHelperExecutableNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AutoShutdown.OfficeSaveHelper",
            "AutoShutdown.TestTreeHelper"
        };

    public static ProcessSelectionDecision Evaluate(
        RunningProcessInfo info,
        ProcessSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(context);

        // 自身进程：绝不选择。
        if (info.ProcessId == context.CurrentProcessId)
        {
            return ProcessSelectionDecision.NotSelectable("AutoShutdown 自身进程，不可选择");
        }

        var path = info.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return ProcessSelectionDecision.NotSelectable("无法读取可执行文件完整路径");
        }

        var trimmed = path.Trim();

        // 不接受相对路径（GetFullPath 会把相对路径伪装成绝对路径，因此必须先查原始路径）。
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return ProcessSelectionDecision.NotSelectable("路径不是完整绝对路径");
        }

        var key = ExecutablePathKey.Normalize(trimmed);
        if (key is null)
        {
            return ProcessSelectionDecision.NotSelectable("路径无效或无法规范化");
        }

        // 环境变量占位路径：拒绝。
        if (key.Contains('%', StringComparison.Ordinal))
        {
            return ProcessSelectionDecision.NotSelectable("路径含环境变量占位符，安全起见不可选择");
        }

        // 已存在于关闭目标列表：显示「已添加」，不得重复添加。
        if (context.ExistingTargetPaths is { } existing)
        {
            foreach (var existingPath in existing)
            {
                if (ExecutablePathKey.EqualsNormalized(key, existingPath))
                {
                    return ProcessSelectionDecision.NotSelectable("已添加");
                }
            }
        }

        // 路径指向现有普通文件；reparse/无法确认一律 fail-closed。
        var fileDecision = CheckOrdinaryFile(key);
        if (fileDecision is not null)
        {
            return fileDecision;
        }

        // 会话：未知或非当前会话 → 不可选择（覆盖 Session 0 / 系统服务 / 其他用户）。
        if (info.SessionId == -1)
        {
            return ProcessSelectionDecision.NotSelectable("无法确认进程会话，安全起见不可选择");
        }

        if (info.SessionId != context.CurrentSessionId)
        {
            return ProcessSelectionDecision.NotSelectable("系统服务 / 其他会话进程，不可选择");
        }

        // AutoShutdown 自身（按路径识别，防御 PID/名称伪装）。
        if (context.CurrentExecutablePath is { } selfPath
            && ExecutablePathKey.EqualsNormalized(key, selfPath))
        {
            return ProcessSelectionDecision.NotSelectable("AutoShutdown 自身进程，不可选择");
        }

        // 辅助进程：按可执行文件基名与进程名双查。
        var fileName = Path.GetFileNameWithoutExtension(key);
        if (KnownHelperExecutableNames.Contains(fileName)
            || KnownHelperExecutableNames.Contains(info.ProcessName))
        {
            return ProcessSelectionDecision.NotSelectable("AutoShutdown 辅助进程，不可选择");
        }

        // Windows 核心系统进程：按进程名与可执行文件基名双查。
        if (CriticalSystemProcessNames.Contains(info.ProcessName)
            || CriticalSystemProcessNames.Contains(fileName))
        {
            return ProcessSelectionDecision.NotSelectable("Windows 系统关键进程，不可选择");
        }

        return ProcessSelectionDecision.Selectable_;
    }

    /// <summary>
    /// 校验规范化路径指向「现有普通文件」。非普通文件 / reparse / 无法确认 → 返回不可选决策；
    /// 全部通过返回 null。
    /// </summary>
    private static ProcessSelectionDecision? CheckOrdinaryFile(string key)
    {
        bool exists;
        try
        {
            exists = File.Exists(key);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return ProcessSelectionDecision.NotSelectable("无法确认路径状态（读取异常）");
        }

        if (!exists)
        {
            return ProcessSelectionDecision.NotSelectable("路径不存在或不可访问");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(key);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // 无法确认路径状态：fail-closed，绝不当作普通文件。
            return ProcessSelectionDecision.NotSelectable("无法确认路径状态（访问拒绝）");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return ProcessSelectionDecision.NotSelectable("路径为符号链接/联接，安全起见不可选择");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return ProcessSelectionDecision.NotSelectable("路径指向目录而非程序文件");
        }

        if ((attributes & FileAttributes.Device) != 0)
        {
            return ProcessSelectionDecision.NotSelectable("路径不是普通文件");
        }

        return null;
    }
}
