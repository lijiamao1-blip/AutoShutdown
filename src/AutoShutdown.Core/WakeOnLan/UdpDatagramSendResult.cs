namespace AutoShutdown.Core.WakeOnLan;

/// <summary>UDP 数据报发送结果（S21）。Socket 错误必须结构化失败，绝不伪造成功。</summary>
public enum UdpDatagramSendStatus
{
    Unknown = 0,
    Success = 1,
    SendFailed = 2
}

public sealed record UdpDatagramSendResult
{
    public UdpDatagramSendStatus Status { get; init; } = UdpDatagramSendStatus.Unknown;

    public string? Error { get; init; }

    public bool Succeeded => Status == UdpDatagramSendStatus.Success;
}
