namespace AutoShutdown.Core.CloseApps;

/// <summary>强杀结局（S18）。强杀前必须复核 PID 与启动时间；身份不明确时绝不强杀。</summary>
public enum ForceKillStatus
{
    Unknown = 0,

    /// <summary>强杀成功。</summary>
    Killed = 1,

    /// <summary>目标进程已退出（竞态），无需强杀。</summary>
    AlreadyExited = 2,

    /// <summary>PID 已重用（启动时间与解析时不符），拒绝强杀。</summary>
    PidReuseDetected = 3,

    /// <summary>访问拒绝（权限不足），无法强杀。</summary>
    AccessDenied = 4,

    /// <summary>其他失败。</summary>
    Failed = 5
}

/// <summary>强杀操作的结构化结果（S18）。</summary>
public sealed record ForceKillResult
{
    public ForceKillStatus Status { get; init; } = ForceKillStatus.Unknown;

    /// <summary>附加说明（脱敏；不含命令行或窗口正文）。</summary>
    public string? Details { get; init; }
}
