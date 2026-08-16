using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 应用窗口关闭边界（S18）。以进程 ID 为键执行窗口/进程级操作：
/// 优雅关闭（WM_CLOSE，经托管 CloseMainWindow）→ 有界等待退出 → 仅在逐目标明确授权时强杀。
/// 强杀前必须复核 PID + 启动时间，防止 PID 重用误杀。服务只依赖此抽象，测试用替身实现。
/// </summary>
public interface IAppWindowManager
{
    /// <summary>目标进程当前是否有主窗口。</summary>
    bool HasMainWindow(int processId);

    /// <summary>向目标进程主窗口发送关闭请求（WM_CLOSE）。返回是否成功投递。</summary>
    bool RequestClose(int processId);

    /// <summary>目标进程是否已退出（进程不存在视为已退出）。</summary>
    bool HasExited(int processId);

    /// <summary>有界等待目标进程退出；超时返回 false。</summary>
    bool WaitForExit(int processId, TimeSpan timeout);

    /// <summary>
    /// 强杀目标进程。实现必须先用 <paramref name="expectedStartTimeUtc"/> 复核当前
    /// PID 对应的启动时间，不一致时返回 <see cref="ForceKillStatus.PidReuseDetected"/>
    /// 而绝不 Kill；访问拒绝返回 <see cref="ForceKillStatus.AccessDenied"/>。
    /// </summary>
    ForceKillResult ForceKill(int processId, DateTimeOffset expectedStartTimeUtc);
}
