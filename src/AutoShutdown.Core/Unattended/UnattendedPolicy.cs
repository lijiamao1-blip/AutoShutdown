using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Unattended;

/// <summary>
/// 无人值守策略 / 版本化授权记录（S20，架构 F7 / I8a）。
/// 不可变记录，持久化为独立文档 <c>unattended.json</c>（区别于 config.json 与 tasks.json）。
/// 默认禁用：缺失记录（NotFound）、损坏（Corrupt）、结构非法（Invalid）、版本不支持
/// （UnsupportedVersion）或授权过期（Expired）一律按 disabled/fail-closed 处理，
/// 绝不静默回退为可能触发任务执行的默认值。
/// </summary>
public sealed record UnattendedPolicy
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>是否启用无人值守。false = 默认禁用 / 已撤销。</summary>
    public bool Enabled { get; init; }

    /// <summary>授权版本号：每次显式启用递增；用于审计与「状态不明一律拒绝」的版本化授权。</summary>
    public int AuthorizationVersion { get; init; }

    /// <summary>最近一次启用授权的时间（UTC）。</summary>
    public DateTimeOffset AuthorizedAtUtc { get; init; }

    /// <summary>被授权的电源动作。</summary>
    public PowerAction AuthorizedAction { get; init; } = PowerAction.Unknown;

    /// <summary>授权过期时间（UTC，可选）。null = 不过期；到期后授权失效。</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>撤销时间（UTC，可选）。置位表示该记录为已撤销态（Enabled=false）。</summary>
    public DateTimeOffset? RevokedAtUtc { get; init; }

    /// <summary>触发/授权原因（脱敏，限长，绝不包含凭据或敏感数据）。</summary>
    public string TriggerReason { get; init; } = string.Empty;
}
