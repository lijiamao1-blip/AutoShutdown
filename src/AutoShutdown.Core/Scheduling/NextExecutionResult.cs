namespace AutoShutdown.Core.Scheduling;

public enum NextExecutionStatus
{
    Unknown = 0,
    Success = 1,
    InvalidTaskKind = 2,
    MissingCountdownDuration = 3,
    InvalidCountdownDuration = 4,
    MissingTargetTimeOfDay = 5,
    NoFutureOccurrence = 6,
    InvalidLocalTime = 7,

    // ---- S14 复杂排程 ----
    /// <summary>Weekdays 规则缺少触发星期集合。</summary>
    MissingWeekdays = 8,

    /// <summary>Weekdays 规则的星期集合为空或不合法。</summary>
    InvalidWeekdays = 9,

    /// <summary>NthWorkdayOfMonth 规则缺少 N。</summary>
    MissingNthWorkday = 10,

    /// <summary>NthWorkdayOfMonth 规则的 N 超出合法范围（1..23）。</summary>
    InvalidNthWorkday = 11,

    /// <summary>OneTime 规则缺少精确 date+time。</summary>
    MissingOneTimeDateTime = 12,

    /// <summary>OneTime 规则的 date+time 已过期（不追溯，应标记 cancelled）。</summary>
    OneTimeExpired = 13,

    /// <summary>在可搜索范围内不存在满足规则的工作日（例如第 N 个工作日长期不存在）。</summary>
    NoWorkdayOccurrence = 14
}

public sealed record NextExecutionResult
{
    public NextExecutionStatus Status { get; init; } = NextExecutionStatus.Unknown;

    public DateTimeOffset? ScheduledFireTime { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == NextExecutionStatus.Success;
}
