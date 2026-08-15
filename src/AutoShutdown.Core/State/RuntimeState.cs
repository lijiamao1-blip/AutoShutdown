namespace AutoShutdown.Core.State;

/// <summary>
/// V2 多实例运行态（架构 §4.1：runtime.json 按任务 id 组织）。
/// 每个任务定义（SourceTaskId）至多一个运行实例；实例按任务 id 组织在
/// <see cref="Instances"/> 中，键 = <see cref="TaskInstance.SourceTaskId"/>。
/// SchemaVersion=2；V1 单实例（CurrentInstance，SchemaVersion=1）的迁移
/// 由 <c>Storage.RuntimeStateStore</c> 按 GATE-Q3「备份后重建」处理。
/// </summary>
public sealed record RuntimeState
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>按任务 id 组织的运行实例集合；键 = 任务定义 id（SourceTaskId）。</summary>
    public IReadOnlyDictionary<Guid, TaskInstance> Instances { get; init; } =
        new Dictionary<Guid, TaskInstance>();

    public DateTimeOffset LastUpdatedAt { get; init; }
}

public sealed record TaskInstance
{
    public Guid InstanceId { get; init; }

    public Guid SourceTaskId { get; init; }

    public PowerAction ActionSnapshot { get; init; } = PowerAction.Unknown;

    /// <summary>V2 8 态任务实例状态（架构 §4.4）。</summary>
    public TaskInstanceState State { get; init; } = TaskInstanceState.Unknown;

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
