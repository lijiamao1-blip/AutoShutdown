namespace AutoShutdown.Core.PrePipeline;

/// <summary>
/// Pre-Pipeline 动作失败时的处理策略（S16）。
/// Block：立即停止后续动作并取消电源意图（fail-closed，默认安全）。
/// Continue：记录失败审计后继续后续动作（电源仍可能执行）。
/// </summary>
public enum FailurePolicy
{
    Unknown = 0,
    Block = 1,
    Continue = 2
}
