namespace AutoShutdown.Core.State;

public sealed record RuntimeState
{
    public int SchemaVersion { get; init; } = 1;

    public TaskInstance? CurrentInstance { get; init; }

    public DateTimeOffset LastUpdatedAt { get; init; }
}

public sealed record TaskInstance
{
    public Guid InstanceId { get; init; }

    public Guid SourceTaskId { get; init; }

    public PowerAction ActionSnapshot { get; init; } = PowerAction.Unknown;

    public TaskState State { get; init; } = TaskState.Unknown;

    public DateTimeOffset ScheduledFireTime { get; init; }

    public DateTimeOffset? WarningStartTime { get; init; }

    public Guid StageToken { get; init; }

    public bool HasExecuted { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>真实电源执行是否已获用户明确确认（双闸门之二）。默认 false，向后兼容。</summary>
    public bool RealPowerConfirmed { get; init; }

    /// <summary>真实电源确认的标识（可选）。默认 null，向后兼容。</summary>
    public Guid? RealPowerConfirmationId { get; init; }
}
