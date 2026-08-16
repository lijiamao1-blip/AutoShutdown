namespace AutoShutdown.Core.Office;

/// <summary>
/// Office 保存辅助进程清理失败的安全故障（S17 独立验收 D4）。在逐应用硬超时/取消期间，
/// 辅助进程无法被终止或无法确认退出时抛出。这是可被上层明确识别的安全失败——绝不能
/// 被当作普通可继续的 TimedOut/NotDetected 结果进入默认 Continue，而必须 fail-closed。
/// 派生自 <see cref="OperationCanceledException"/>：外部取消场景仍以 OCE 语义传播
/// （不被转 NotDetected），下游用本类型区分「安全故障」与「普通取消/超时」。
/// </summary>
public sealed class OfficeHelperCleanupFailedException : OperationCanceledException
{
    public OfficeHelperCleanupFailedException(string message)
        : base(message)
    {
    }

    public OfficeHelperCleanupFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
