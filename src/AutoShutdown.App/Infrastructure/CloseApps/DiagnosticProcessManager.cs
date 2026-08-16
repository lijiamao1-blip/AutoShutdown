using System.Diagnostics;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Infrastructure.CloseApps;

/// <summary>
/// 基于 System.Diagnostics.Process 的真实进程身份读取（S18）。真实进程枚举的唯一边界；
/// 不引入任何 P/Invoke、进程启动、命令解释器或 shell。读取可执行路径/会话/启动时间时
/// 对访问拒绝（Win32Exception）、已退出进程（InvalidOperationException/ArgumentException）做
/// 防御性容错：无法读取时返回 null / -1 / default，绝不把异常泄漏给编排层。
/// </summary>
public sealed class DiagnosticProcessManager : IProcessManager
{
    public int CurrentProcessId => Environment.ProcessId;

    public int CurrentSessionId => Process.GetCurrentProcess().SessionId;

    public IReadOnlyList<ProcessSnapshot> EnumerateProcesses()
    {
        var snapshots = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                snapshots.Add(SnapshotOf(process));
            }
        }

        return snapshots;
    }

    public ProcessSnapshot? GetProcessById(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null; // 进程不存在（已退出）。
        }

        using (process)
        {
            return SnapshotOf(process);
        }
    }

    private static ProcessSnapshot SnapshotOf(Process process) => new()
    {
        ProcessId = process.Id,
        ProcessName = ReadProcessName(process),
        ExecutablePath = ReadExecutablePath(process),
        SessionId = ReadSessionId(process),
        StartTimeUtc = ReadStartTime(process)
    };

    private static string ReadProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ReadExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null; // 访问拒绝 / 已退出：身份不明确。
        }
    }

    private static int ReadSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch
        {
            return -1; // 未知：会话检查将按「非当前会话」跳过（安全）。
        }
    }

    private static DateTimeOffset ReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch
        {
            return default; // 未知：强杀前复核将拒绝（安全）。
        }
    }
}
