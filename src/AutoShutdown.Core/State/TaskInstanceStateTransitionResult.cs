namespace AutoShutdown.Core.State;

/// <summary>V2 状态转换决策码。</summary>
public enum TaskInstanceStateTransitionDecisionCode
{
    Allowed = 0,
    UnknownCurrentState = 1,
    UnknownTargetState = 2,
    UnknownCause = 3,
    TransitionNotAllowed = 4,
    CauseMismatch = 5
}

/// <summary>
/// V2 状态转换的结构化结果。拒绝结果携带调用方记录 Error 审计日志所需的全部字段：
/// 当前状态、目标状态、触发原因、调用来源、决策码、时间戳、原因说明。
/// 纯状态机自身不引入日志依赖、不写日志；日志记录责任归具备日志依赖的调用边界。
/// </summary>
public sealed record TaskInstanceStateTransitionResult
{
    public bool Allowed { get; init; }

    public TaskInstanceState CurrentState { get; init; }

    public TaskInstanceState TargetState { get; init; }

    public TaskInstanceStateTransitionCause Cause { get; init; }

    public TaskInstanceStateTransitionDecisionCode DecisionCode { get; init; }

    /// <summary>调用来源（由调用方提供，供审计日志），如 "SchedulerEngine"、"TaskService"。</summary>
    public string? Source { get; init; }

    /// <summary>决策时间戳（UTC），供调用方写入审计日志。</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>原因/说明，足以支撑 Error 审计日志。</summary>
    public string Reason { get; init; } = string.Empty;
}
