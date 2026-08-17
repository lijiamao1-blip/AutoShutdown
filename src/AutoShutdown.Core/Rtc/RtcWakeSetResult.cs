namespace AutoShutdown.Core.Rtc;

/// <summary>一次性 RTC 唤醒设置结果状态（S21）。</summary>
public enum RtcWakeSetStatus
{
    Unknown = 0,
    Success = 1,

    /// <summary>平台不支持该场景（如仅能唤醒睡眠/休眠，而本次为完全关机）。</summary>
    Unsupported = 2,

    /// <summary>权限不足（如缺少武装系统唤醒定时器所需的特权）。</summary>
    NotAuthorized = 3,

    /// <summary>系统/固件拒绝了唤醒请求。</summary>
    FirmwareRejected = 4,
    Cancelled = 5
}

/// <summary>
/// RTC 唤醒设置结果（S21）。Unsupported / NotAuthorized / FirmwareRejected / Cancelled /
/// Unknown 一律为明确失败；只有 Success 才算设置成功（绝不伪造成功）。
/// </summary>
public sealed record RtcWakeSetResult
{
    public RtcWakeSetStatus Status { get; init; } = RtcWakeSetStatus.Unknown;

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == RtcWakeSetStatus.Success;
}
