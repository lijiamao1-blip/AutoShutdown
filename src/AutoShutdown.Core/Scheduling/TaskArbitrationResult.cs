namespace AutoShutdown.Core.Scheduling;

/// <summary>
/// 仲裁决策结果（契约字段来自 S13-T06 卡，T04 仅定义并消费）。
/// </summary>
public sealed record TaskArbitrationResult
{
    /// <summary>赢家任务 id（唯一执行者）；null 表示无自动赢家（需用户决策）。</summary>
    public Guid? WinnerTaskId { get; init; }

    /// <summary>合并执行的任务 id（当前契约下通常为空，供 T06 扩展）。</summary>
    public IReadOnlyList<Guid> MergedTaskIds { get; init; } = [];

    /// <summary>被重排（延后）的任务 id。</summary>
    public IReadOnlyList<Guid> RescheduledTaskIds { get; init; } = [];

    /// <summary>是否需要用户人工决策。</summary>
    public bool RequiresUserDecision { get; init; }

    /// <summary>决策理由（审计 / 日志 / UI 展示）。</summary>
    public string DecisionReason { get; init; } = string.Empty;
}
