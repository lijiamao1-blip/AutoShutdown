namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// 规范化后的关闭目标（S18）。由 <see cref="CloseAppsTargetList.Normalize"/> 从配置生成：
/// 身份已校验（可执行路径 或 进程 ID 恰取其一），超时转为 <see cref="TimeSpan"/>，
/// <see cref="TargetId"/> 为脱敏标签（仅文件名或 pid:N，不含全路径/命令行/窗口标题）。
/// </summary>
public sealed record CloseAppTarget
{
    /// <summary>脱敏标签（仅文件名或 pid:N）。</summary>
    public string TargetId { get; init; } = string.Empty;

    public string? ExecutablePath { get; init; }

    public int? ProcessId { get; init; }

    public bool ForceKillAllowed { get; init; }

    public TimeSpan GracefulTimeout { get; init; }
}
