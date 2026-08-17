using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Unattended;

/// <summary>
/// 无人值守策略服务。评估统一走 <see cref="EvaluateAsync"/>（fail-closed）；启用/撤销走
/// <see cref="UnattendedAuthorizationStore"/>（原子写入 + 备份证据）。
/// 远程层只能读取 <see cref="EvaluateAsync"/> 结论，不得写入策略（S23 未实现，本服务无网络接口）。
/// </summary>
public sealed class UnattendedPolicyService : IUnattendedPolicyService
{
    private readonly UnattendedAuthorizationStore _store;
    private readonly IClock _clock;

    public UnattendedPolicyService(
        UnattendedAuthorizationStore store,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    public async Task<UnattendedAuthorizationDecision> EvaluateAsync(
        PowerAction action,
        CancellationToken cancellationToken)
    {
        if (action == PowerAction.Unknown)
        {
            return UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.ActionMismatch,
                "An unknown power action is never authorized.");
        }

        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        return load.Status switch
        {
            UnattendedLoadStatus.NotFound => UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.NotFound,
                "No unattended authorization record exists."),
            UnattendedLoadStatus.Corrupt => UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.Corrupt,
                "The unattended authorization record is corrupt: " + JoinErrors(load.Errors)),
            UnattendedLoadStatus.IoFailure => UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.Unavailable,
                "The unattended authorization record is unavailable: " + JoinErrors(load.Errors)),
            UnattendedLoadStatus.Invalid => UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.Invalid,
                "The unattended authorization record is invalid: " + JoinErrors(load.Errors)),
            UnattendedLoadStatus.UnsupportedVersion => UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.UnsupportedVersion,
                "The unattended authorization record uses an unsupported schema version: " + JoinErrors(load.Errors)),
            UnattendedLoadStatus.Success => EvaluateLoaded(load.Policy!, action),
            _ => UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.Unknown,
                "The unattended authorization record returned an unknown status."),
        };
    }

    public async Task<UnattendedEnableResult> EnableAsync(
        UnattendedEnableRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.SecondConfirmationCompleted)
        {
            return new UnattendedEnableResult
            {
                Status = UnattendedEnableStatus.MissingSecondConfirmation,
                Error = "Enabling unattended mode requires a completed local second confirmation."
            };
        }

        if (request.Action == PowerAction.Unknown)
        {
            return new UnattendedEnableResult
            {
                Status = UnattendedEnableStatus.InvalidAction,
                Error = "A valid power action is required to enable unattended mode."
            };
        }

        if (request.ExpiresAtUtc is { } expires && expires <= _clock.UtcNow)
        {
            return new UnattendedEnableResult
            {
                Status = UnattendedEnableStatus.InvalidRequest,
                Error = "The authorization expiry must be in the future."
            };
        }

        // 版本递增：即便现有记录损坏/非法，也由原子写入备份旧文件作为可恢复证据，
        // 绝不静默覆盖丢失审计痕迹。显式启用是经二次确认的用户动作，非静默回退。
        var existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var nextVersion = existing.Status == UnattendedLoadStatus.Success
            ? existing.Policy!.AuthorizationVersion + 1
            : 1;

        var policy = new UnattendedPolicy
        {
            SchemaVersion = UnattendedPolicy.CurrentSchemaVersion,
            Enabled = true,
            AuthorizationVersion = nextVersion,
            AuthorizedAtUtc = _clock.UtcNow,
            AuthorizedAction = request.Action,
            ExpiresAtUtc = request.ExpiresAtUtc,
            TriggerReason = SanitizeReason(request.TriggerReason)
        };

        var save = await _store.SaveAsync(policy, cancellationToken).ConfigureAwait(false);
        if (!save.Succeeded)
        {
            return new UnattendedEnableResult
            {
                Status = UnattendedEnableStatus.IoFailure,
                Error = "Failed to write the unattended authorization: " + JoinErrors(save.Errors)
            };
        }

        return new UnattendedEnableResult
        {
            Status = UnattendedEnableStatus.Success,
            Policy = policy
        };
    }

    public async Task<UnattendedRevokeResult> RevokeAsync(CancellationToken cancellationToken)
    {
        var existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (existing.Status == UnattendedLoadStatus.NotFound)
        {
            // 无可撤销记录：视为成功（无副作用），不伪造记录。
            return new UnattendedRevokeResult { Status = UnattendedRevokeStatus.Success };
        }

        if (existing.Status != UnattendedLoadStatus.Success || existing.Policy is null)
        {
            // 损坏/非法/不可用记录无法可靠撤销：保守失败，要求人工介入，绝不静默覆盖证据。
            return new UnattendedRevokeResult
            {
                Status = UnattendedRevokeStatus.IoFailure,
                Error = "The unattended authorization record is not in a revocable state: " + JoinErrors(existing.Errors)
            };
        }

        var revoked = existing.Policy with
        {
            Enabled = false,
            AuthorizedAction = PowerAction.Unknown,
            RevokedAtUtc = _clock.UtcNow
        };

        var save = await _store.SaveAsync(revoked, cancellationToken).ConfigureAwait(false);
        if (!save.Succeeded)
        {
            return new UnattendedRevokeResult
            {
                Status = UnattendedRevokeStatus.IoFailure,
                Error = "Failed to write the revocation: " + JoinErrors(save.Errors)
            };
        }

        return new UnattendedRevokeResult
        {
            Status = UnattendedRevokeStatus.Success,
            Policy = revoked
        };
    }

    private UnattendedAuthorizationDecision EvaluateLoaded(
        UnattendedPolicy policy,
        PowerAction action)
    {
        if (policy.RevokedAtUtc is not null)
        {
            return UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.Revoked,
                "The unattended authorization was revoked.",
                policy);
        }

        if (!policy.Enabled)
        {
            return UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.Disabled,
                "Unattended mode is disabled.",
                policy);
        }

        if (policy.ExpiresAtUtc is { } expires && _clock.UtcNow >= expires)
        {
            return UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.Expired,
                "The unattended authorization has expired.",
                policy);
        }

        if (policy.AuthorizedAction != action)
        {
            return UnattendedAuthorizationDecision.Denied(
                UnattendedPolicyStatus.ActionMismatch,
                $"The unattended authorization does not cover action {action}.",
                policy);
        }

        return UnattendedAuthorizationDecision.Granted(policy);
    }

    private static string SanitizeReason(string reason)
        => (reason ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

    private static string JoinErrors(IReadOnlyList<string> errors)
        => errors.Count == 0 ? "unknown error." : string.Join(" ", errors);
}
