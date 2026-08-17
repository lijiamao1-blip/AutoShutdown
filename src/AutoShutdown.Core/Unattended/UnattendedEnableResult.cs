using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Unattended;

/// <summary>
/// 启用无人值守的请求。本地二次确认由 UI 层完成两次独立确认后置
/// <see cref="SecondConfirmationCompleted"/>；服务据此拒绝未经二次确认的启用（fail-closed）。
/// </summary>
public sealed record UnattendedEnableRequest
{
    /// <summary>要授权的电源动作。</summary>
    public PowerAction Action { get; init; } = PowerAction.Unknown;

    /// <summary>授权/触发原因（脱敏，限长）。</summary>
    public string TriggerReason { get; init; } = string.Empty;

    /// <summary>可选过期时间；null = 不过期。</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>本地二次确认已完成标记；false 时服务拒绝启用。</summary>
    public bool SecondConfirmationCompleted { get; init; }
}

public enum UnattendedEnableStatus
{
    Unknown = 0,
    Success = 1,
    MissingSecondConfirmation = 2,
    InvalidAction = 3,
    InvalidRequest = 4,
    IoFailure = 5
}

public sealed record UnattendedEnableResult
{
    public UnattendedEnableStatus Status { get; init; } = UnattendedEnableStatus.Unknown;

    public UnattendedPolicy? Policy { get; init; }

    public string? Error { get; init; }

    public bool Succeeded => Status == UnattendedEnableStatus.Success;
}

public enum UnattendedRevokeStatus
{
    Unknown = 0,
    Success = 1,
    IoFailure = 2
}

public sealed record UnattendedRevokeResult
{
    public UnattendedRevokeStatus Status { get; init; } = UnattendedRevokeStatus.Unknown;

    public UnattendedPolicy? Policy { get; init; }

    public string? Error { get; init; }

    public bool Succeeded => Status == UnattendedRevokeStatus.Success;
}
