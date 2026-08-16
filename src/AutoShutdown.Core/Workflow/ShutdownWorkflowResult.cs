using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;

namespace AutoShutdown.Core.Workflow;

public sealed record ShutdownWorkflowResult
{
    public ShutdownWorkflowStatus Status { get; init; } = ShutdownWorkflowStatus.Unknown;

    public ShutdownDecisionCode DecisionCode { get; init; } = ShutdownDecisionCode.Unknown;

    public PowerResult? PowerResult { get; init; }

    /// <summary>Pre-Pipeline 运行结果（S16）。仅当流水线已运行时非 null；携带逐动作审计。</summary>
    public PrePipelineRunResult? PrePipeline { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status is ShutdownWorkflowStatus.Simulated or ShutdownWorkflowStatus.Accepted;
}
