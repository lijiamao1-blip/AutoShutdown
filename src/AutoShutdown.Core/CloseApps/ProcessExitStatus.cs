namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// 进程退出状态三态（S18-D1）。区分「确定已退出 / 确定仍在运行 / 无法确认」，
/// 避免把「无法确认」伪装成「已退出」（D1-3）。服务据此 fail-closed：无法确认时
/// 绝不当作已退出、绝不强杀、绝不当成成功关闭。
/// </summary>
public enum ProcessExitStatus
{
    /// <summary>无法确认（查询失败 / 访问拒绝 / 状态不可读）。安全上按「非已退出」处理。</summary>
    Unknown = 0,

    /// <summary>进程已退出（不存在或已结束）。</summary>
    Exited = 1,

    /// <summary>进程仍在运行。</summary>
    Running = 2
}
