namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// 优雅等待退出结果三态（S18-D2）。区分「确定已退出 / 确定仍在运行并达到等待期限 / 无法确认」，
/// 避免把等待异常后「退出状态无法确认」折叠成普通超时并继续强杀。服务据此 fail-closed：
/// <see cref="Unknown"/> 绝不当作 <see cref="TimedOut"/>、绝不强杀。
/// </summary>
public enum ProcessWaitResult
{
    /// <summary>无法确认（等待异常且退出状态不可读）。安全上按「非已退出且非可确认超时」处理。</summary>
    Unknown = 0,

    /// <summary>进程已退出（优雅关闭成功或等待期间自然退出）。</summary>
    Exited = 1,

    /// <summary>确定进程仍在运行，且已到达等待期限（或等待异常但复核明确仍在运行）。</summary>
    TimedOut = 2
}
