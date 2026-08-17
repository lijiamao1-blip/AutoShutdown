namespace AutoShutdown.Core.Unattended;

/// <summary>
/// 无人值守授权评估状态。除 <see cref="Authorized"/> 外，其余状态一律 fail-closed
/// （视为未授权 / disabled），仅保留具体状态供审计与 UI 呈现。
/// </summary>
public enum UnattendedPolicyStatus
{
    Unknown = 0,

    /// <summary>已授权：Enabled=true 且版本匹配、未过期、动作匹配。</summary>
    Authorized = 1,

    /// <summary>无授权记录（unattended.json 不存在）。</summary>
    NotFound = 2,

    /// <summary>记录存在但未启用（Enabled=false，且未标记撤销）。</summary>
    Disabled = 3,

    /// <summary>记录存在但已撤销（Enabled=false 且 RevokedAtUtc 已置位）。</summary>
    Revoked = 4,

    /// <summary>授权已过期（ExpiresAtUtc 早于当前时间）。</summary>
    Expired = 5,

    /// <summary>授权动作与本次请求动作不匹配。</summary>
    ActionMismatch = 6,

    /// <summary>记录 JSON 损坏，无法解析。</summary>
    Corrupt = 7,

    /// <summary>记录结构非法（缺字段/非法值）。</summary>
    Invalid = 8,

    /// <summary>记录 schema 版本不受支持。</summary>
    UnsupportedVersion = 9,

    /// <summary>存储层不可用（IO 失败等）。</summary>
    Unavailable = 10
}
