namespace AutoShutdown.Core.Workflow;

public sealed class ScheduledTaskHandlingException : Exception
{
    public ScheduledTaskHandlingException(ShutdownWorkflowResult result)
        : base("The shutdown workflow rejected the due task.")
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
    }

    /// <summary>仅消息构造（S21：WoL 执行失败时经此类型标记实例 Faulted，不携带电源结果）。</summary>
    public ScheduledTaskHandlingException(string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(message);
    }

    public ShutdownWorkflowResult? Result { get; }
}
