namespace AutoShutdown.Core.Rtc;

/// <summary>RTC 唤醒能力状态（S21）。</summary>
public enum RtcWakeCapabilityStatus
{
    Unknown = 0,
    Supported = 1,
    NotSupported = 2
}

/// <summary>
/// 平台可提供的唤醒范围（S21）。唤醒范围必须诚实声明：仅能从睡眠/休眠唤醒的平台
/// 无法在完全关机（S5）后把机器唤醒，不得伪装支持。
/// </summary>
public enum RtcWakeScope
{
    Unknown = 0,

    /// <summary>可从睡眠（S3）/ 休眠（S4）唤醒。</summary>
    Suspend = 1,

    /// <summary>可从完全关机（S5，需固件 RTC 闹钟）唤醒。</summary>
    SuspendAndPowerOff = 2
}

public sealed record RtcWakeCapabilityResult
{
    public RtcWakeCapabilityStatus Status { get; init; } = RtcWakeCapabilityStatus.Unknown;

    public RtcWakeScope WakeScope { get; init; } = RtcWakeScope.Unknown;

    /// <summary>诚实、可诊断的能力说明（含唤醒范围边界，如 S5 是否可用）。</summary>
    public string Reason { get; init; } = string.Empty;

    public bool Supported => Status == RtcWakeCapabilityStatus.Supported;
}
