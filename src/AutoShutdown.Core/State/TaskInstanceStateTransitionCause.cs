namespace AutoShutdown.Core.State;

/// <summary>
/// V2 状态转换触发原因（16 个触发场景口径，GATE-A2）。
/// 白名单为 15 条唯一状态边；其中 waiting→cancelled 有两个触发场景
/// （CancelByUser 与 OneTimeExpired，后者由 V2-DRAFT-004 修正 6d 补充），
/// 故共 16 个触发场景。
/// </summary>
public enum TaskInstanceStateTransitionCause
{
    Unknown = 0,

    /// <summary>waiting → running：排程触发，进入 Pre-Pipeline。</summary>
    ScheduleTriggered = 1,

    /// <summary>waiting → cancelled：用户取消未触发任务。</summary>
    CancelByUser = 2,

    /// <summary>waiting → cancelled：系统判定一次性任务过期（修正 6d）。</summary>
    OneTimeExpired = 3,

    /// <summary>waiting → faulted / confirming → faulted：配置损坏检测。</summary>
    ConfigCorrupt = 4,

    /// <summary>running → confirming：Pre-Pipeline 完成，进入 Countdown。</summary>
    PipelineCompleted = 5,

    /// <summary>running → cancelled：Pre-Pipeline 失败且 failurePolicy=block。</summary>
    PipelineFailedBlocked = 6,

    /// <summary>running/confirming/executing → interrupted：崩溃恢复。</summary>
    CrashRecovered = 7,

    /// <summary>running → faulted：运行时配置损坏。</summary>
    RuntimeConfigCorrupt = 8,

    /// <summary>confirming → executing：用户确认 / 无人值守触发。</summary>
    PowerConfirmed = 9,

    /// <summary>confirming → cancelled：用户取消 / 倒计时超时且无人值守关。</summary>
    CancelledDuringConfirmation = 10,

    /// <summary>executing → executed：电源操作完成。</summary>
    PowerCompleted = 11,

    /// <summary>executing → faulted：执行阶段异常。</summary>
    PowerFailed = 12,

    /// <summary>executed → waiting：重复任务下一轮排程（刷新 StageToken）。</summary>
    Reschedule = 13
}
