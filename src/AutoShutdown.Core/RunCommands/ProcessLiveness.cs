namespace AutoShutdown.Core.RunCommands;

/// <summary>进程存活复核三态（S19-D1）。枚举/状态查询失败或身份无法确认一律 Unknown（fail-closed），
/// 绝不把「未知」当作「已退出」而误报成功。</summary>
public enum ProcessLiveness
{
    /// <summary>身份/状态无法确认（fail-closed）。</summary>
    Unknown = 0,

    /// <summary>原进程仍存活。</summary>
    Alive = 1,

    /// <summary>原进程已退出（含 PID 被系统复用为不同进程的情形）。</summary>
    Exited = 2
}
