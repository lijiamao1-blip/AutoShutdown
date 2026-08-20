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

    // ---- S-CLOSEUI1 只读识别信息（仅展示与候选提示；绝不参与执行期匹配） ----
    // 由「从运行中的进程选择」在添加时记录，帮助用户识别目标与在路径失效后辅助搜索；
    // 执行期匹配的唯一依据始终是 ExecutablePath（规范化完整绝对路径）。缺失（旧配置/
    // 手工添加）为 null，绝不因此改变执行匹配或校验结果。

    /// <summary>添加时的进程名（仅识别用）。</summary>
    public string? ProcessName { get; init; }

    /// <summary>添加时的产品名称（仅识别用）。</summary>
    public string? ProductName { get; init; }

    /// <summary>添加时的公司名称（仅识别用）。</summary>
    public string? CompanyName { get; init; }

    /// <summary>添加时的窗口标题（仅识别用；窗口标题会变化，绝不作为执行依据）。</summary>
    public string? WindowTitleAtAdd { get; init; }

    /// <summary>添加时间（UTC，仅识别用）。</summary>
    public DateTimeOffset? AddedAtUtc { get; init; }
}
