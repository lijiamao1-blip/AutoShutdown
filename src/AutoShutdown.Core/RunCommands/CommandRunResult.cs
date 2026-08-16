namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 单命令执行结果（S19）。结构化、可审计；Output/ErrorOutput 为限量截断后的原始流内容，
/// 不进入日志（报告摘要只含脱敏状态标签，不含参数/完整输出/凭据）。
/// </summary>
public sealed record CommandRunResult
{
    public CommandRunStatus Status { get; init; } = CommandRunStatus.Unknown;

    public int? ExitCode { get; init; }

    /// <summary>限量 stdout（截断至上限）。</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>限量 stderr（截断至上限）。</summary>
    public string ErrorOutput { get; init; } = string.Empty;

    /// <summary>stdout/stderr 是否因超出上限被截断。</summary>
    public bool OutputTruncated { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>整树清理失败的结构化脱敏原因（仅清理未确认时非空；测试/诊断用，无敏感内容）。</summary>
    public CommandCleanupFailureReason? CleanupFailureReason { get; init; }

    /// <summary>脱敏状态描述（无参数、无完整输出、无凭据）。</summary>
    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == CommandRunStatus.Success;
}
