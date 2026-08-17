namespace AutoShutdown.Core.Unattended;

/// <summary>
/// 无人值守授权评估结论。唯一允许继续电源意图的情况是 <see cref="Status"/> ==
/// <see cref="UnattendedPolicyStatus.Authorized"/>（此时 <see cref="IsAuthorized"/> 为 true）。
/// 其余一切状态（NotFound/Disabled/Revoked/Expired/ActionMismatch/Corrupt/Invalid/
/// UnsupportedVersion/Unavailable/Unknown）均 fail-closed，绝不放行电源。
/// </summary>
public sealed record UnattendedAuthorizationDecision
{
    public bool IsAuthorized { get; init; }

    public UnattendedPolicyStatus Status { get; init; } = UnattendedPolicyStatus.Unknown;

    /// <summary>评估命中的策略快照（仅当可解析时非 null；审计用）。</summary>
    public UnattendedPolicy? Policy { get; init; }

    /// <summary>脱敏的评估说明，供审计与 UI 呈现（绝不包含凭据或敏感数据）。</summary>
    public string Reason { get; init; } = string.Empty;

    public static UnattendedAuthorizationDecision Denied(
        UnattendedPolicyStatus status,
        string reason,
        UnattendedPolicy? policy = null) => new()
        {
            IsAuthorized = false,
            Status = status,
            Policy = policy,
            Reason = reason
        };

    public static UnattendedAuthorizationDecision Granted(UnattendedPolicy policy) => new()
    {
        IsAuthorized = true,
        Status = UnattendedPolicyStatus.Authorized,
        Policy = policy,
        Reason = "The unattended policy authorizes this action."
    };
}
