namespace AutoShutdown.Core.Unattended;

/// <summary>无人值守等效确认的结局。</summary>
public enum UnattendedConfirmationOutcome
{
    Denied = 0,
    EquivalentConfirmation = 1
}

/// <summary>
/// 无人值守等效确认决策（Countdown 倒计时边界的统一评估结论）。
/// 只有 <see cref="Outcome"/> == <see cref="UnattendedConfirmationOutcome.EquivalentConfirmation"/>
/// （且授权有效、实例未终结）才允许替代人工确认；其余一律 Denied。
/// </summary>
public sealed record UnattendedConfirmationDecision
{
    public UnattendedConfirmationOutcome Outcome { get; init; } = UnattendedConfirmationOutcome.Denied;

    /// <summary>脱敏决策说明，供审计与 UI 呈现。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>命中的授权决策（仅当等效确认时非 null）。</summary>
    public UnattendedAuthorizationDecision? Authorization { get; init; }

    public bool AllowsPower => Outcome == UnattendedConfirmationOutcome.EquivalentConfirmation;
}
