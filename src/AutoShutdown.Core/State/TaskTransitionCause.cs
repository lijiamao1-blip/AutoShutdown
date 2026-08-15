namespace AutoShutdown.Core.State;

public enum TaskTransitionCause
{
    Unknown = 0,
    Schedule = 1,
    WarningDue = 2,
    ExecuteDue = 3,
    CancelByUser = 4,
    SnoozeByUser = 5,
    Reschedule = 6,
    PowerAccepted = 7,
    PowerFailed = 8,
    RecoveryInterrupted = 9,
    ClearTerminalState = 10
}
