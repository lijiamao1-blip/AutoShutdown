namespace AutoShutdown.Core.Workflow;

public sealed class ScheduledTaskHandlingException : Exception
{
    public ScheduledTaskHandlingException(ShutdownWorkflowResult result)
        : base("The shutdown workflow rejected the due task.")
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
    }

    public ShutdownWorkflowResult Result { get; }
}
