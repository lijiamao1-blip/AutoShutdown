using AutoShutdown.Core.Power;

namespace AutoShutdown.Core.Workflow;

public sealed record ShutdownWorkflowResult
{
    public ShutdownWorkflowStatus Status { get; init; } = ShutdownWorkflowStatus.Unknown;

    public ShutdownDecisionCode DecisionCode { get; init; } = ShutdownDecisionCode.Unknown;

    public PowerResult? PowerResult { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status is ShutdownWorkflowStatus.Simulated or ShutdownWorkflowStatus.Accepted;
}
