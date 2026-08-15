namespace AutoShutdown.Core.Workflow;

public enum ShutdownWorkflowStatus
{
    Unknown = 0,
    Simulated = 1,
    Rejected = 2,
    PowerFailed = 3,
    Accepted = 4
}
