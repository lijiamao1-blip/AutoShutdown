using AutoShutdown.Core.Office;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// Office 保存辅助进程启动网关抽象（S17 独立验收 D2）。主程序只能通过此网关启动
/// 辅助进程；原生进程启动仅存在于唯一实现
/// <see cref="OfficeSaveHelperLauncher"/> 中，绝不散落他处。自动化测试注入替身，
/// 不启动真实进程。
/// </summary>
public interface IOfficeSaveHelperLauncher
{
    /// <summary>
    /// 启动辅助进程处理单个 Office 应用。路径解析/校验失败、进程启动失败时抛异常
    /// （调用方据此返回可诊断失败，不阻断流程）。
    /// </summary>
    IOfficeSaveHelperProcess Launch(OfficeApplicationKind application, string correlationId);
}

/// <summary>
/// 已启动的辅助进程句柄。父进程可轮询退出、读取固定结果、在超时/取消时终止整棵
/// 进程树并确认退出。仅封装辅助进程（AutoShutdown.OfficeSaveHelper），绝不触及用户 Office。
/// </summary>
public interface IOfficeSaveHelperProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>在指定毫秒内等待退出；返回是否已退出。</summary>
    bool WaitForExit(int milliseconds);

    /// <summary>读取全部标准输出（固定最小结果行，可能为空）。</summary>
    string ReadStandardOutput();

    /// <summary>终止整棵进程树（仅辅助进程及其子进程，绝不终止 WINWORD/EXCEL/POWERPNT）。</summary>
    void KillTree();

    /// <summary>终止后等待确认退出；返回是否在期限内确认退出。</summary>
    bool WaitForExitAfterKill(TimeSpan timeout);
}
