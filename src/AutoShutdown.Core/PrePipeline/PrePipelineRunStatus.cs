namespace AutoShutdown.Core.PrePipeline;

/// <summary>Pre-Pipeline 整体运行状态（S16）。</summary>
public enum PrePipelineRunStatus
{
    Unknown = 0,

    /// <summary>全部成功，或 continue 失败已记录但流水线走完。</summary>
    Completed = 1,

    /// <summary>block 动作失败，流水线立即停止，电源意图取消。</summary>
    Blocked = 2,

    /// <summary>运行中被取消（无副作用，直接传播取消）。</summary>
    Cancelled = 3
}
