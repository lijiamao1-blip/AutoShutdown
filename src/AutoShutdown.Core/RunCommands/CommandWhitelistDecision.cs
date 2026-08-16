namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 白名单授权结果（S19）。Allowed 为真时才可执行，且必须使用规范化后的绝对路径
/// （NormalizedExecutablePath）启动，避免大小写/分隔符/相对段差异绕过。
/// </summary>
public sealed record CommandWhitelistDecision
{
    public bool Allowed { get; init; }

    public CommandRejectionReason RejectionReason { get; init; } = CommandRejectionReason.Unknown;

    /// <summary>授权通过时给出的规范化绝对路径（无相对段、统一分隔符）。</summary>
    public string NormalizedExecutablePath { get; init; } = string.Empty;
}
