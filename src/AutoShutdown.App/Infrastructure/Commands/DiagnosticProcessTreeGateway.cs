using System.Diagnostics;
using System.Management;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.RunCommands;

namespace AutoShutdown.App.Infrastructure.Commands;

/// <summary>
/// 基于托管 WMI（Win32_Process ParentProcessId）与 System.Diagnostics.Process 的真实进程树网关（S19-D1）。
/// 经 WMI 构建父子关系以枚举根进程及全部后代，并以「PID + 启动时间（UTC）」作为稳定身份；
/// 只做枚举与存活查询，绝不启动/终止任何进程（终止由 CommandRunner 的 Process.Kill(entireProcessTree) 完成）。
/// 查询异常一律 fail-closed：枚举失败抛出、存活无法确认返回 Unknown；不引入 P/Invoke、shell 或电源依赖。
/// </summary>
public sealed class DiagnosticProcessTreeGateway : IProcessTreeGateway
{
    public IReadOnlyList<ProcessIdentity> SnapshotTree(int rootProcessId)
    {
        var childrenOf = BuildChildrenMap();

        var members = new List<int>();
        var visited = new HashSet<int> { rootProcessId };
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            members.Add(current);

            if (!childrenOf.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (visited.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return members.Select(IdentityOf).ToList();
    }

    public ProcessLiveness CheckLiveness(ProcessIdentity identity)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
        }
        catch (ArgumentException)
        {
            return ProcessLiveness.Exited; // PID 已不存在。
        }

        using (process)
        {
            var currentStartTime = ReadStartTime(process);
            return DecideLiveness(
                identity,
                processExists: true,
                currentStartTime,
                currentStartTimeKnown: currentStartTime != default);
        }
    }

    /// <summary>
    /// 纯身份匹配判断（可测）：PID 已不存在 → Exited；启动时间无法确认 → Unknown（fail-closed）；
    /// 启动时间一致 → Alive；不一致 → Exited（PID 被系统复用为新进程，原进程已退出，绝不误杀新进程）。
    /// </summary>
    internal static ProcessLiveness DecideLiveness(
        ProcessIdentity identity,
        bool processExists,
        DateTimeOffset currentStartTimeUtc,
        bool currentStartTimeKnown)
    {
        if (!processExists)
        {
            return ProcessLiveness.Exited;
        }

        if (!identity.StartTimeKnown || !currentStartTimeKnown)
        {
            return ProcessLiveness.Unknown;
        }

        return currentStartTimeUtc == identity.StartTimeUtc
            ? ProcessLiveness.Alive
            : ProcessLiveness.Exited;
    }

    private static Dictionary<int, List<int>> BuildChildrenMap()
    {
        var childrenOf = new Dictionary<int, List<int>>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId FROM Win32_Process");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                // 任意记录无法读取 ProcessId / ParentProcessId → 整次快照失败（fail-closed）。
                // 绝不 continue 静默跳过而生成不完整快照：不完整快照可能漏掉后代，
                // 从而把「后代未确认退出」误判为整树已退出。
                var (processId, parentProcessId) = ParseProcessRecord(obj["ProcessId"], obj["ParentProcessId"]);

                if (!childrenOf.TryGetValue(parentProcessId, out var children))
                {
                    children = [];
                    childrenOf[parentProcessId] = children;
                }

                children.Add(processId);
            }
        }

        return childrenOf;
    }

    /// <summary>
    /// 解析单条 Win32_Process 记录的 ProcessId / ParentProcessId（可测缝）。
    /// 任一值为 null 或不可转换为 int → 抛 <see cref="InvalidOperationException"/>，
    /// 使整次快照失败（fail-closed），绝不静默跳过。
    /// </summary>
    internal static (int ProcessId, int ParentProcessId) ParseProcessRecord(
        object? processIdValue,
        object? parentProcessIdValue)
    {
        if (processIdValue is null || parentProcessIdValue is null)
        {
            throw new InvalidOperationException(
                "A Win32_Process record has null identifiers; the process tree snapshot failed.");
        }

        try
        {
            return (Convert.ToInt32(processIdValue), Convert.ToInt32(parentProcessIdValue));
        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException or InvalidCastException)
        {
            throw new InvalidOperationException(
                "A Win32_Process record could not be parsed; the process tree snapshot failed.",
                exception);
        }
    }

    private static ProcessIdentity IdentityOf(int processId)
    {
        DateTimeOffset startTime;
        try
        {
            using var process = Process.GetProcessById(processId);
            startTime = ReadStartTime(process);
        }
        catch
        {
            startTime = default; // 已退出/访问拒绝：身份不明确（fail-closed）。
        }

        return new ProcessIdentity { ProcessId = processId, StartTimeUtc = startTime };
    }

    private static DateTimeOffset ReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch
        {
            return default; // 访问拒绝/已退出：启动时间未知。
        }
    }
}
