using AutoShutdown.Core.RunCommands;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 进程树受控快照网关（S19-D1）。CommandRunner 唯一依赖的进程树枚举/存活查询抽象：
/// 终止前枚举根进程及全部后代并保存稳定身份（PID + 启动时间），终止后有界确认全部身份已退出。
/// 本抽象只做「枚举」与「存活查询」，绝不启动/终止进程（终止由调用方的 Process.Kill 完成）。
/// 枚举失败抛出异常、存活无法确认返回 Unknown，由调用方 fail-closed；测试以替身注入，避免真机枚举。
/// </summary>
public interface IProcessTreeGateway
{
    /// <summary>
    /// 枚举以 <paramref name="rootProcessId"/> 为根的整棵进程树（含根）并保存稳定身份。
    /// 枚举失败应抛出异常（调用方 fail-closed）。
    /// </summary>
    IReadOnlyList<ProcessIdentity> SnapshotTree(int rootProcessId);

    /// <summary>
    /// 复核单个进程身份是否已退出（PID 已不存在或启动时间不一致 → Exited；一致 → Alive；
    /// 无法确认 → Unknown）。不终止任何进程。
    /// </summary>
    ProcessLiveness CheckLiveness(ProcessIdentity identity);
}
