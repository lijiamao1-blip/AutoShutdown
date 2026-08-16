namespace AutoShutdown.Core.CloseApps;

/// <summary>单个目标关闭结局（S18）。区分优雅关闭、已退出、未运行、无窗口、超时、
/// 强杀、访问拒绝、受保护跳过与 PID 重用，形成结构化可审计结果。</summary>
public enum CloseAppStatus
{
    Unknown = 0,

    /// <summary>优雅关闭成功（WM_CLOSE 后按期退出）。</summary>
    ClosedGracefully = 1,

    /// <summary>目标进程在关闭前已退出（竞态）。</summary>
    AlreadyExited = 2,

    /// <summary>未匹配到任何进程（目标未运行）。</summary>
    NotRunning = 3,

    /// <summary>匹配到进程但无主窗口，无法优雅关闭。</summary>
    NoWindow = 4,

    /// <summary>优雅关闭超时，且未授权强杀。</summary>
    TimedOut = 5,

    /// <summary>已授权且 PID+启动时间复核通过后强杀成功。</summary>
    ForceKilled = 6,

    /// <summary>访问拒绝，无法关闭/强杀。</summary>
    AccessDenied = 7,

    /// <summary>命中自身/系统关键/非当前会话进程，安全跳过。</summary>
    SkippedProtected = 8,

    /// <summary>PID 已重用（启动时间不符），拒绝操作。</summary>
    PidReuseDetected = 9,

    /// <summary>已发起强杀但退出未确认（D1-4）：绝不把未确认的强杀当成成功。</summary>
    ExitNotConfirmed = 10,

    /// <summary>退出状态无法确认（D1-3）：绝不当作已退出，也绝不强杀。</summary>
    ExitStatusUnknown = 11
}
