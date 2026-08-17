namespace AutoShutdown.Core.State;

public enum TaskKind
{
    Unknown = 0,
    Countdown = 1,
    TodayAt = 2,
    DailyAt = 3,

    /// <summary>每周工作日（周期）：在指定星期（周一~周五，可叠加节假日例外）的固定时刻触发。</summary>
    Weekdays = 4,

    /// <summary>下个工作日 N 点（一次性）：下一个工作日（周一~周五，排除节假日）的固定时刻触发。</summary>
    NextWorkday = 5,

    /// <summary>每月第 N 个工作日（周期）：每月第 N 个工作日（周一~周五，排除节假日）的固定时刻触发。</summary>
    NthWorkdayOfMonth = 6,

    /// <summary>一次性指定日期时间（schedule.type=oneday）：date+time 精确；过期即终结，不追溯执行。</summary>
    OneTime = 7,

    /// <summary>空闲关机（S15）：系统连续无输入达到阈值后触发；用户恢复输入取消空闲倒计时。</summary>
    Idle = 8
}

public enum PowerAction
{
    Unknown = 0,
    Shutdown = 1,
    Restart = 2,
    Sleep = 3,
    Hibernate = 4,

    /// <summary>网络唤醒（S21）：经调度器作为显式任务类型触发，不经双闸门/真实电源。</summary>
    WakeOnLan = 5
}

public enum TaskState
{
    Unknown = 0,
    Idle = 1,
    Scheduled = 2,
    Warning = 3,
    Executing = 4,
    Cancelled = 5,
    Completed = 6,
    Failed = 7,
    Interrupted = 8
}

public enum PowerOutcome
{
    Unknown = 0,
    Accepted = 1,
    Rejected = 2,
    Failed = 3,
    Simulated = 4
}

public enum LogLevel
{
    Unknown = 0,
    Information = 1,
    Warning = 2,
    Error = 3
}
