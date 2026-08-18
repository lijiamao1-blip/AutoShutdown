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

    /// <summary>
    /// 该实例的倒计时是否由空闲触发形成（S15）。仅空闲触发的 confirming/countdown
    /// 在输入恢复时被取消；非空闲任务此标志恒为 false，不受影响。
    /// </summary>
    public bool IsIdleTriggered { get; init; }

    /// <summary>
    /// 该实例是否因输入恢复（空闲时长回落到阈值以下）而被取消（S15）。仅由空闲触发
    /// 且被恢复取消的实例置真；用户手动取消或其它原因取消保持 false。向后兼容默认 false。
    /// </summary>
    public bool IsIdleRecovered { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>真实电源执行是否已获用户明确确认（双闸门之二）。默认 false，向后兼容。</summary>
    public bool RealPowerConfirmed { get; init; }

    /// <summary>真实电源确认的标识（可选）。默认 null，向后兼容。</summary>
    public Guid? RealPowerConfirmationId { get; init; }

    /// <summary>
    /// 该实例是否显式选择无人值守执行（S20-D1）。默认 false。仅当为 true 时，调度器在
    /// 倒计时边界评估无人值守等效确认；否则维持人工确认路径（RealPowerConfirmed）。
    /// </summary>
    public bool UseUnattended { get; init; }

    /// <summary>
    /// WoL 目标机器 id 快照（S21，ActionSnapshot==WakeOnLan 时必填）。由任务定义
    /// <see cref="TaskDefinition.TargetMachineId"/> 在实例创建时复制；调度器经 handler
    /// 派发到 WoL 执行器，只向用户显式配置的局域网目标发送。
    /// </summary>
    public Guid? TargetMachineId { get; init; }

    /// <summary>
    /// 一次性 RTC 唤醒时间快照（UTC，S21）。由任务定义
    /// <see cref="TaskDefinition.RtcWakeTimeUtc"/> 在实例创建时复制；ShutdownWorkflow
    /// 填入 Pre-Pipeline 上下文，由 RtcWakeAction 作为受控关机前步骤处理。
    /// </summary>
    public DateTimeOffset? RtcWakeTimeUtc { get; init; }

    /// <summary>
    /// 任务定义当前是否启用（S-UI2 多任务 UI 呈现）。由 <see cref="TaskDefinition.IsEnabled"/>
    /// 在实例创建时复制，并在 <c>SetTaskEnabledCommand</c> 时随定义同步更新并持久化。
    /// 停用任务不会触发执行；旧 runtime.json 无此字段时按 true（启用）向后兼容。
    /// 仅为 UI/运行态呈现，不影响引擎执行裁决（执行仍以 TaskCollection 定义为唯一事实源）。
    /// </summary>
    public bool IsEnabled { get; init; } = true;
}
