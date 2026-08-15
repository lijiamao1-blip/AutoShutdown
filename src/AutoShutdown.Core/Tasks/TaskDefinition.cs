using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Tasks;

public sealed record TaskDefinition
{
    public Guid Id { get; init; }

    public TaskKind Kind { get; init; } = TaskKind.Unknown;

    public PowerAction Action { get; init; } = PowerAction.Unknown;

    public TimeSpan? CountdownDuration { get; init; }

    public TimeOnly? TargetTimeOfDay { get; init; }

    public int? WarningSeconds { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>真实电源执行是否已获用户明确确认（双闸门之二）。默认 false。</summary>
    public bool RealPowerConfirmed { get; init; }
}
