namespace AutoShutdown.Core.State;

/// <summary>
/// V2 8-state task instance state（架构 V2-DRAFT-004 §4.4 / S13 §5）。
/// <list type="bullet">
/// <item>Waiting - 初始态，等待排程触发（V1 Idle/Scheduled 合并）</item>
/// <item>Running - 正在执行（Pre-Pipeline 中）</item>
/// <item>Confirming - 等待用户确认（Countdown 中）</item>
/// <item>Executing - 正在执行电源操作</item>
/// <item>Executed - 已完成（终态，可重排）</item>
/// <item>Cancelled - 被取消（终态）</item>
/// <item>Faulted - 故障（终态）</item>
/// <item>Interrupted - 崩溃恢复（终态，永不补执行）</item>
/// </list>
/// 与 V1 的映射见架构书 §4.4（Idle/Scheduled→Waiting、Warning→Confirming、
/// Completed→Executed、Failed→Faulted）。
/// </summary>
public enum TaskInstanceState
{
    Unknown = 0,
    Waiting = 1,
    Running = 2,
    Confirming = 3,
    Executing = 4,
    Executed = 5,
    Cancelled = 6,
    Faulted = 7,
    Interrupted = 8
}
