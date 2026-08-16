namespace AutoShutdown.Core.Configuration;

/// <summary>
/// CloseApps 配置段（S18）。向后兼容：旧配置无此段时取默认值（空目标 = 不关闭任何应用）。
/// 强杀默认关闭（ForceKillAllowed 逐目标显式 opt-in）。损坏/非法目标由
/// <see cref="ConfigurationValidator"/> 与 <see cref="AutoShutdown.Core.CloseApps.CloseAppsTargetList"/>
/// 双重校验，绝不静默回退为可能触发关闭/强杀的默认值。
/// </summary>
public sealed record CloseAppsConfig
{
    /// <summary>全局优雅关闭等待期限（秒）。默认 30。</summary>
    public int GracefulTimeoutSeconds { get; init; } = 30;

    public CloseAppsTargetConfig[] Targets { get; init; } = [];
}

/// <summary>单个待关闭目标（S18）。稳定标识：可执行路径 或 进程 ID，二者取其一。</summary>
public sealed record CloseAppsTargetConfig
{
    /// <summary>可执行文件全路径（精确匹配，大小写不敏感）。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>进程 ID（精确匹配）。</summary>
    public int? ProcessId { get; init; }

    /// <summary>逐目标强杀授权（默认 false，未授权强杀关闭）。</summary>
    public bool ForceKillAllowed { get; init; }

    /// <summary>逐目标优雅关闭等待期限覆盖（秒）；未设置用全局值。</summary>
    public int? GracefulTimeoutSeconds { get; init; }
}
