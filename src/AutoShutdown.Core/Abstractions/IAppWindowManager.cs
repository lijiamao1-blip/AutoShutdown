using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 应用窗口关闭边界（S18）。以进程 ID 为键执行窗口/进程级操作：
/// 优雅关闭（WM_CLOSE，经托管 CloseMainWindow）→ 可取消的有界等待退出 → 仅在逐目标明确授权时强杀。
/// 强杀前必须复核 PID + 启动时间，防止 PID 重用误杀；强杀后必须确认退出，未确认绝不返回成功。
/// 退出状态为三态（Unknown/Exited/Running），避免把「无法确认」伪装成「已退出」。服务只依赖此抽象。
/// </summary>
public interface IAppWindowManager
{
    /// <summary>目标进程当前是否有主窗口。</summary>
    bool HasMainWindow(int processId);

    /// <summary>向目标进程主窗口发送关闭请求（WM_CLOSE）。返回是否成功投递。</summary>
    bool RequestClose(int processId);

    /// <summary>
    /// 三态退出状态。实现不得在查询异常时返回 Exited（会把「无法确认」伪装成「已退出」）；
    /// 无法确认时返回 <see cref="ProcessExitStatus.Unknown"/>（fail-closed）。
    /// </summary>
    ProcessExitStatus GetExitStatus(int processId);

    /// <summary>
    /// 可取消的有界等待进程退出；true = 已退出，false = 超时仍在运行。
    /// 取消时抛出 <see cref="OperationCanceledException"/>，绝不吞掉取消后继续强杀。
    /// </summary>
    bool WaitForExit(int processId, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// 强杀目标进程。实现必须先用 <paramref name="expectedStartTimeUtc"/> 复核当前
    /// PID 对应的启动时间，不一致时返回 <see cref="ForceKillStatus.PidReuseDetected"/>
    /// 而绝不 Kill；访问拒绝返回 <see cref="ForceKillStatus.AccessDenied"/>；
    /// Kill 后必须确认退出，未确认返回 <see cref="ForceKillStatus.ExitNotConfirmed"/>。
    /// </summary>
    ForceKillResult ForceKill(int processId, DateTimeOffset expectedStartTimeUtc);
}
