namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// RunCommands 总体报告（S19）。<see cref="Succeeded"/> 仅当不存在「阻断级失败」（Block 策略
/// 命令失败/被拒绝）；Continue 策略命令失败仅记录审计、不阻断。Summary 为脱敏、限长的失败摘要，
/// 只含索引/状态标签/白名单化可执行路径与拒绝原因，绝不含参数、完整输出或凭据。
/// </summary>
public sealed record RunCommandsReport
{
    /// <summary>配置的命令总数。</summary>
    public int CommandCount { get; init; }

    /// <summary>实际启动（经白名单授权后交给执行器）的命令数。</summary>
    public int ExecutedCount { get; init; }

    /// <summary>成功（退出码 0）的命令数。</summary>
    public int SucceededCount { get; init; }

    /// <summary>逐命令结果（仅含已尝试的命令；被 Block 阻断后剩余的未执行命令不出现在此）。</summary>
    public IReadOnlyList<RunCommandResultEntry> Results { get; init; } =
        Array.Empty<RunCommandResultEntry>();

    public bool Succeeded { get; init; }

    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// 单个命令的审计结果（S19）。ExecutablePath 为白名单化绝对路径（非敏感）；绝不记录
/// Arguments / stdout / stderr 全文，防止 secret/token/敏感参数进入日志。
/// </summary>
public sealed record RunCommandResultEntry
{
    /// <summary>命令在配置中的位置（0 起）。</summary>
    public int Index { get; init; }

    /// <summary>白名单化绝对路径（非敏感）。</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>是否真正启动并运行（false = 被白名单拒绝，未执行）。</summary>
    public bool Executed { get; init; }

    /// <summary>执行结局（仅 Executed 时有意义）。</summary>
    public CommandRunStatus Status { get; init; } = CommandRunStatus.Unknown;

    /// <summary>白名单拒绝原因（仅被拒绝时设置）。</summary>
    public CommandRejectionReason RejectionReason { get; init; } = CommandRejectionReason.Unknown;

    public int? ExitCode { get; init; }

    public TimeSpan Duration { get; init; }

    public bool Succeeded { get; init; }
}
