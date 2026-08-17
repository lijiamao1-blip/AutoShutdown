using AutoShutdown.Core.State;
using AutoShutdown.Core.Unattended;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 无人值守策略服务（S20）。负责版本化授权记录的读取评估与本地启停。
/// 远程层（S23，未实现）只能消费 <see cref="EvaluateAsync"/> 的只读结论，
/// 绝不得调用 <see cref="EnableAsync"/> / <see cref="RevokeAsync"/> 写入策略。
/// </summary>
public interface IUnattendedPolicyService
{
    /// <summary>
    /// 评估指定电源动作是否获无人值守授权。任何异常（NotFound/Corrupt/Invalid/
    /// UnsupportedVersion/Disabled/Revoked/Expired/ActionMismatch/Unavailable）
    /// 均 fail-closed，返回 <see cref="UnattendedAuthorizationDecision.IsAuthorized"/> == false。
    /// </summary>
    Task<UnattendedAuthorizationDecision> EvaluateAsync(
        PowerAction action,
        CancellationToken cancellationToken);

    /// <summary>本地启用（要求显式二次确认），写入版本化授权记录（原子 + 备份证据）。</summary>
    Task<UnattendedEnableResult> EnableAsync(
        UnattendedEnableRequest request,
        CancellationToken cancellationToken);

    /// <summary>本地撤销（立即失效），原子写入撤销态记录。</summary>
    Task<UnattendedRevokeResult> RevokeAsync(CancellationToken cancellationToken);
}
