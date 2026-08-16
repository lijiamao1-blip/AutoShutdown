namespace AutoShutdown.Core.PrePipeline;

/// <summary>Pre-Pipeline 整体运行结果（S16）。PowerAllowed 决定是否进入唯一电源出口。</summary>
public sealed record PrePipelineRunResult
{
    public PrePipelineRunStatus Status { get; init; } = PrePipelineRunStatus.Unknown;

    public IReadOnlyList<PrePipelineActionResult> Actions { get; init; } =
        Array.Empty<PrePipelineActionResult>();

    /// <summary>仅 Completed 允许进入电源出口；Blocked/Cancelled 一律取消电源意图。</summary>
    public bool PowerAllowed => Status == PrePipelineRunStatus.Completed;
}
