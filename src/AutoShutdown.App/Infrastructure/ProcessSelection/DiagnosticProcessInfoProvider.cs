using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AutoShutdown.App.Infrastructure.ProcessSelection;

/// <summary>
/// 基于 System.Diagnostics.Process 的只读进程信息探测（S-CLOSEUI1）。真实进程枚举的 UI 边界：
/// 复用既有只读枚举思路（<c>Process.GetProcesses()</c> + 逐字段防御性容错），并扩展窗口标题/
/// 产品名称/公司名称（<see cref="FileVersionInfo"/>，只读打开 EXE 版本资源）。不引入任何
/// P/Invoke、进程启动、关闭、命令解释器或 shell；不申请管理员权限。
/// 单条进程任一步读取失败：该字段置空/置默认哨兵，绝不抛出中断整表（由调用方按行标灰）。
/// </summary>
public sealed class DiagnosticProcessInfoProvider : IProcessInfoProvider
{
    public int CurrentProcessId => Environment.ProcessId;

    public int CurrentSessionId => ReadSessionId(Process.GetCurrentProcess());

    public string? CurrentExecutablePath => Environment.ProcessPath;

    public IReadOnlyList<RunningProcessInfo> EnumerateProcesses()
    {
        var snapshots = new List<RunningProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                snapshots.Add(SnapshotOf(process));
            }
        }

        return snapshots;
    }

    public RunningProcessInfo? GetById(int processId)
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
        catch (Win32Exception)
        {
            return null; // 访问拒绝（受保护进程）：按「无法确认」处理，fail-closed。
        }

        using (process)
        {
            return SnapshotOf(process);
        }
    }

    private static RunningProcessInfo SnapshotOf(Process process)
    {
        var info = new RunningProcessInfo
        {
            ProcessId = process.Id,
            ProcessName = ReadProcessName(process),
            ExecutablePath = ReadExecutablePath(process),
            SessionId = ReadSessionId(process),
            WindowTitle = ReadWindowTitle(process),
            HasMainWindow = ReadHasMainWindow(process),
            StartTimeUtc = ReadStartTime(process)
        };

        // 产品/公司名称只在路径可读时尝试（只读版本资源；失败置空）。
        if (info.ExecutablePath is { } path)
        {
            var versionInfo = ReadVersionInfo(path);
            info = info with
            {
                ProductName = versionInfo?.ProductName ?? string.Empty,
                CompanyName = versionInfo?.CompanyName ?? string.Empty
            };
        }

        return info;
    }

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
            return null; // 访问拒绝 / 已退出：路径无法确认。
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
            return -1; // 未知：会话检查按「非当前会话」跳过（安全）。
        }
    }

    private static string ReadWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ReadHasMainWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != 0
                || !string.IsNullOrWhiteSpace(process.MainWindowTitle);
        }
        catch
        {
            return false; // 无法确认窗口状态：不因此误判为可选。
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
            return default;
        }
    }

    private static FileVersionInfo? ReadVersionInfo(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path);
        }
        catch
        {
            return null; // 访问拒绝 / 文件被占用 / 非 PE：产品信息不可读。
        }
    }
}
