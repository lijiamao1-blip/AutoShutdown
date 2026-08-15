namespace AutoShutdown.Core.State;

public enum TaskKind
{
    Unknown = 0,
    Countdown = 1,
    TodayAt = 2,
    DailyAt = 3
}

public enum PowerAction
{
    Unknown = 0,
    Shutdown = 1,
    Restart = 2,
    Sleep = 3,
    Hibernate = 4
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
