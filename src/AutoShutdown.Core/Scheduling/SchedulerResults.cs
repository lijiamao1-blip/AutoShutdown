using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling;

public enum SchedulerEngineStatus
{
    Unknown = 0,
    Created = 1,
    Running = 2,
    Faulted = 3,
    Stopped = 4
}

public enum SchedulerCommandStatus
{
    Unknown = 0,
    Success = 1,
    NotRunning = 2,
    Faulted = 3,
    NoCurrentTask = 4,
    ActiveTaskExists = 5,
    StaleCommand = 6,
    TaskServiceRejected = 7,
    PersistenceFailed = 8,
    InvalidCommand = 9,
    TransitionRejected = 10
}

/// <summary>
/// 调度引擎对外快照。V2 多实例：以任务 id（SourceTaskId）为键的实例集合，
/// 替代 V1 单实例 CurrentInstance。
/// </summary>
public sealed record SchedulerSnapshot
{
    public static SchedulerSnapshot Empty { get; } = new();

    public SchedulerEngineStatus EngineStatus { get; init; } = SchedulerEngineStatus.Unknown;

    /// <summary>运行实例集合，键 = 任务定义 id（SourceTaskId）。</summary>
    public IReadOnlyDictionary<Guid, TaskInstance> Instances { get; init; } =
        new Dictionary<Guid, TaskInstance>();

    public DateTimeOffset LastUpdatedAt { get; init; }

    public string? FaultMessage { get; init; }

    /// <summary>待用户决策的强制冲突仲裁（无则 null）。</summary>
    public PendingArbitration? PendingArbitration { get; init; }

    /// <summary>最近一次仲裁结果（供 UI 呈现；瞬态，不持久化）。</summary>
    public ArbitrationOutcome? LastArbitration { get; init; }
}

public sealed record SchedulerCommandResult
{
    public SchedulerCommandStatus Status { get; init; } = SchedulerCommandStatus.Unknown;

    public TaskCommandStatus? TaskCommandStatus { get; init; }

    public TaskInstanceStateTransitionDecisionCode? TransitionDecisionCode { get; init; }

    public SchedulerSnapshot Snapshot { get; init; } = SchedulerSnapshot.Empty;

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == SchedulerCommandStatus.Success;
}

/// <summary>
/// 待决仲裁（强制冲突需用户决策）：引擎不 SetFaulted，而是挂起等待
/// <see cref="ResolveArbitrationCommand"/>；UI 呈现候选并询问用户。
/// 瞬态，不持久化。
/// </summary>
public sealed record PendingArbitration
{
    /// <summary>参与本次冲突的候选任务 id（含未来赢家）。</summary>
    public IReadOnlyList<Guid> CandidateTaskIds { get; init; } = [];

    public string DecisionReason { get; init; } = string.Empty;
}

/// <summary>
/// 最近一次仲裁结果（仲裁结果呈现；瞬态，不持久化）。
/// </summary>
public sealed record ArbitrationOutcome
{
    public Guid? WinnerTaskId { get; init; }

    /// <summary>合并进赢家执行的任务 id（当前契约下同动作合并）。</summary>
    public IReadOnlyList<Guid> MergedTaskIds { get; init; } = [];

    /// <summary>被改期（延后）的任务 id。</summary>
    public IReadOnlyList<Guid> RescheduledTaskIds { get; init; } = [];

    public bool RequiresUserDecision { get; init; }

    public string DecisionReason { get; init; } = string.Empty;
}
