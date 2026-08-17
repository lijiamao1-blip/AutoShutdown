using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.Unattended;

namespace AutoShutdown.Core.Workflow;

public sealed record ShutdownWorkflowResult
{
    public ShutdownWorkflowStatus Status { get; init; } = ShutdownWorkflowStatus.Unknown;

    public ShutdownDecisionCode DecisionCode { get; init; } = ShutdownDecisionCode.Unknown;

    public PowerResult? PowerResult { get; init; }

    /// <summary>Pre-Pipeline 运行结果（S16）。仅当流水线已运行时非 null；携带逐动作审计。</summary>
    public PrePipelineRunResult? PrePipeline { get; init; }

    /// <summary>
    /// 无人值守等效确认决策（S20，审计字段）。仅当真实电源路径命中无人值守等效确认时非 null；
    /// 携带授权版本/授权时刻/触发原因（经 <see cref="UnattendedConfirmationDecision.Authorization"/>）。
    /// 人工确认或测试模式时为 null。
    /// </summary>
    public UnattendedConfirmationDecision? UnattendedConfirmation { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status is ShutdownWorkflowStatus.Simulated or ShutdownWorkflowStatus.Accepted;
}
