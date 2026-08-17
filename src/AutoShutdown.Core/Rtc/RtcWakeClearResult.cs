namespace AutoShutdown.Core.Rtc;

/// <summary>一次性 RTC 唤醒清除结果状态（S21）。</summary>
public enum RtcWakeClearStatus
{
    Unknown = 0,
    Success = 1,
    NotAuthorized = 2,
    Cancelled = 3,

    /// <summary>清除失败（取消/关闭底层定时器失败）。清除失败必须明确上报。</summary>
    CleanupFailed = 4
}

/// <summary>RTC 唤醒清除结果（S21）。清除失败（CleanupFailed）必须明确上报，绝不静默。</summary>
public sealed record RtcWakeClearResult
{
    public RtcWakeClearStatus Status { get; init; } = RtcWakeClearStatus.Unknown;

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == RtcWakeClearStatus.Success;
}
