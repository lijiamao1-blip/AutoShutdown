namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// 单个目标的关闭结果（S18）。结构化、可审计；不含命令行、窗口正文、文档内容或无关路径。
/// </summary>
public sealed record CloseAppTargetResult
{
    /// <summary>脱敏标签（仅文件名或 pid:N）。</summary>
    public string TargetId { get; init; } = string.Empty;

    /// <summary>单一匹配进程的 PID（多实例匹配或未匹配时为 null）。</summary>
    public int? ProcessId { get; init; }

    /// <summary>匹配到的进程数量。</summary>
    public int MatchedCount { get; init; }

    public CloseAppStatus Status { get; init; } = CloseAppStatus.Unknown;

    /// <summary>该目标是否被授权强杀（审计用）。</summary>
    public bool ForceKillAuthorized { get; init; }

    public bool Succeeded { get; init; }
}
