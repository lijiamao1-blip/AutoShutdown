using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 进程枚举与身份读取边界（S18）。服务只依赖此抽象，绝不直接触碰 System.Diagnostics.Process；
/// 自动化测试用替身实现，绝不枚举或操纵真实进程。真实实现须对访问拒绝、已退出进程做
/// 防御性容错（返回 null/默认哨兵），绝不把异常泄漏给编排层。
/// </summary>
public interface IProcessManager
{
    /// <summary>当前进程 ID（自身保护）。</summary>
    int CurrentProcessId { get; }

    /// <summary>当前用户会话 ID（会话限制）。</summary>
    int CurrentSessionId { get; }

    /// <summary>枚举所有进程的身份快照。</summary>
    IReadOnlyList<ProcessSnapshot> EnumerateProcesses();

    /// <summary>按进程 ID 重新读取身份；进程不存在（已退出）时返回 null。</summary>
    ProcessSnapshot? GetProcessById(int processId);
}
