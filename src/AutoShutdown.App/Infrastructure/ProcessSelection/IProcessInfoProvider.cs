using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Infrastructure.ProcessSelection;

/// <summary>
/// 进程选择窗口的只读进程信息（S-CLOSEUI1）。与 CloseApps 执行边界的
/// <see cref="ProcessSnapshot"/> 分离：额外携带窗口标题、产品名称、公司名称等 UI 识别信息，
/// 但同样只读取、绝不启动/关闭/终止任何进程，绝不发窗口消息，绝不请求提权。
/// 单条读取失败以空/null 呈现（由调用方按行标灰），绝不抛给上层导致整表失败。
/// </summary>
public sealed record RunningProcessInfo
{
    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    /// <summary>可执行文件完整绝对路径；无法读取时为 null。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>进程会话 ID；未知为 -1。</summary>
    public int SessionId { get; init; }

    /// <summary>主窗口标题；无窗口或读取失败为空串。</summary>
    public string WindowTitle { get; init; } = string.Empty;

    /// <summary>是否有可确认的主窗口（MainWindowHandle 非零 或 窗口标题非空）。只读探测。</summary>
    public bool HasMainWindow { get; init; }

    /// <summary>产品名称（来自可执行文件版本信息）；无法读取为空串。</summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>公司名称（来自可执行文件版本信息）；无法读取为空串。</summary>
    public string CompanyName { get; init; } = string.Empty;

    /// <summary>进程启动时间（UTC）；未知为 default。</summary>
    public DateTimeOffset StartTimeUtc { get; init; }
}

/// <summary>
/// 进程选择窗口使用的只读进程枚举边界（S-CLOSEUI1）。生产实现 <see cref="DiagnosticProcessInfoProvider"/>
/// 基于托管 System.Diagnostics.Process，只读；自动化测试用替身注入，绝不枚举或操纵真实进程。
/// </summary>
public interface IProcessInfoProvider
{
    /// <summary>当前进程 ID（自身保护）。</summary>
    int CurrentProcessId { get; }

    /// <summary>当前用户会话 ID（会话限制）。</summary>
    int CurrentSessionId { get; }

    /// <summary>AutoShutdown 自身可执行文件完整路径（用于自身识别）；未知为 null。</summary>
    string? CurrentExecutablePath { get; }

    /// <summary>枚举全部进程的只读信息快照。</summary>
    IReadOnlyList<RunningProcessInfo> EnumerateProcesses();

    /// <summary>按 PID 重新读取信息；进程已退出时返回 null。</summary>
    RunningProcessInfo? GetById(int processId);
}
