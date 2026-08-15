using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Power;

public sealed record PowerResult
{
    public PowerOutcome Outcome { get; init; } = PowerOutcome.Unknown;
    public bool WasSimulated { get; init; }
    public int? NativeErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
}
