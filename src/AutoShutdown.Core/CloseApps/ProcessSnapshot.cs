namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// 进程身份快照（S18）。只携带稳定标识（进程 ID、可执行文件全路径、会话、启动时间），
/// 不含命令行、窗口标题、环境变量等敏感/模糊信息。可执行路径无法读取（访问拒绝或已退出）
/// 时为 null；会话未知时为 -1；启动时间未知时为 default。
/// </summary>
public sealed record ProcessSnapshot
{
    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    /// <summary>可执行文件全路径；无法读取时为 null。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>进程会话 ID；未知为 -1。</summary>
    public int SessionId { get; init; }

    /// <summary>进程启动时间（UTC）；未知为 default。</summary>
    public DateTimeOffset StartTimeUtc { get; init; }
}
