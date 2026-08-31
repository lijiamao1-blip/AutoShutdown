namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// CloseApps 总体报告（S18）。<see cref="Succeeded"/> 仅当全部目标成功关闭/已退出/未运行；
/// 任一失败目标（超时/访问拒绝/无窗口/受保护跳过/PID 重用）即视为失败。Summary 为脱敏、
/// 限长的失败摘要，不含全路径/命令行/窗口正文。
/// </summary>
public sealed record CloseAppsReport
{
    public int TargetCount { get; init; }

    public IReadOnlyList<CloseAppTargetResult> Results { get; init; } =
        Array.Empty<CloseAppTargetResult>();

    public bool Succeeded { get; init; }

    /// <summary>
    /// 目标关闭存在失败时，是否因用户已明确开启“最终强制完成系统关机”而允许电源流程继续。
    /// </summary>
    public bool ContinueToPower { get; init; }

    public string Summary { get; init; } = string.Empty;
}
