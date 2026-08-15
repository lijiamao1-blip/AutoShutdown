using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Recovery;

/// <summary>
/// 崩溃恢复结果状态。
/// </summary>
public enum CrashRecoveryStatus
{
    Unknown = 0,

    /// <summary>检测到瞬态实例（running/confirming/executing）并已中断、已落盘一次。</summary>
    Recovered = 1,

    /// <summary>未发现瞬态实例，无需写入。</summary>
    NoRecoveryNeeded = 2,

    NotFound = 3,
    Corrupt = 4,
    IoFailure = 5,
    Invalid = 6,
    UnsupportedVersion = 7,

    /// <summary>某个瞬态实例的中断转换被状态机拒绝（冻结白名单下不应发生）。</summary>
    TransitionRejected = 8
}

/// <summary>
/// 崩溃恢复结果：载入结果 + 被中断实例的 task id（供审计 Warning 使用）。
/// 纯数据，不写日志、不触发通知；日志与呈现由调用方（App 层）负责。
/// </summary>
public sealed record CrashRecoveryResult
{
    public CrashRecoveryStatus Status { get; init; } = CrashRecoveryStatus.Unknown;

    /// <summary>恢复后的运行态（Success/Migrated 路径）；失败时为 null。</summary>
    public RuntimeState? State { get; init; }

    /// <summary>被中断为 interrupted 的瞬态实例的 task id（键 = SourceTaskId）。</summary>
    public IReadOnlyList<Guid> InterruptedTaskIds { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Succeeded => Status is CrashRecoveryStatus.Recovered or CrashRecoveryStatus.NoRecoveryNeeded;
}
