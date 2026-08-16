namespace AutoShutdown.Core.PrePipeline;

/// <summary>
/// 单个 Pre-Pipeline 动作的结果（S16）。携带脱敏错误与耗时供审计追溯；
/// 错误文本不含命令参数、文档内容或凭据。
/// </summary>
public sealed record PrePipelineActionResult
{
    public string ActionName { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public FailurePolicy FailurePolicy { get; init; } = FailurePolicy.Unknown;

    /// <summary>动作开始时刻（UTC），供审计。</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>动作耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>脱敏错误信息（失败时）。不含命令参数/文档内容/凭据。</summary>
    public string ErrorMessage { get; init; } = string.Empty;
}
