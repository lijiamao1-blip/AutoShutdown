using AutoShutdown.Core.State;

namespace AutoShutdown.Core.PrePipeline;

/// <summary>
/// Pre-Pipeline 动作的上下文（S16）。仅暴露任务/审计所需信息；
/// 绝不暴露 IPowerService——动作只能返回结果，不能直接调用电源，
/// 也不能自行改变任务终态。
/// </summary>
public sealed record PrePipelineContext
{
    public Guid InstanceId { get; init; }

    public Guid SourceTaskId { get; init; }

    public PowerAction Action { get; init; } = PowerAction.Unknown;

    public DateTimeOffset ScheduledFireTime { get; init; }

    /// <summary>一次性 RTC 唤醒时间（UTC，S21）。null = 本次流程不设置 RTC 唤醒。</summary>
    public DateTimeOffset? RtcWakeTimeUtc { get; init; }
}
