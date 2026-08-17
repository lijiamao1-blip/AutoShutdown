namespace AutoShutdown.Core.WakeOnLan;

/// <summary>Wake-on-LAN 发送结果状态（S21）。</summary>
public enum WolSendStatus
{
    Unknown = 0,
    Success = 1,
    TargetNotFound = 2,
    InvalidTarget = 3,
    SendFailed = 4
}

public sealed record WolSendResult
{
    public WolSendStatus Status { get; init; } = WolSendStatus.Unknown;

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == WolSendStatus.Success;

    public static WolSendResult Success(string message) => new()
    {
        Status = WolSendStatus.Success,
        Message = message
    };

    public static WolSendResult Failure(WolSendStatus status, string message) => new()
    {
        Status = status,
        Message = message
    };
}
