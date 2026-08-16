namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 外部取消路径上「命令进程树清理未确认」的可识别异常（S19-D1）。
/// 仍继承 <see cref="OperationCanceledException"/>，保证取消仍沿 OCE 通道传播（不污染调用方判取消的逻辑）；
/// 携带「进程树清理未确认/清理失败」语义，供上层审计区分「已确认取消」与「取消但清理未确认」。
/// 绝不携带参数 / secret / token / 完整输出。
/// </summary>
public sealed class CommandCleanupFailedException : OperationCanceledException
{
    public CommandCleanupFailedException(string message)
        : base(message)
    {
    }

    public CommandCleanupFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CommandCleanupFailedException(string message, Exception innerException, CancellationToken token)
        : base(message, innerException, token)
    {
    }
}
