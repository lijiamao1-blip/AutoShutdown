using System.ComponentModel;
using System.Diagnostics;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Infrastructure.CloseApps;

/// <summary>
/// 基于 System.Diagnostics.Process 的真实窗口/进程关闭（S18）。优雅关闭走托管
/// <see cref="Process.CloseMainWindow"/>（等价 WM_CLOSE 到主窗口）；强杀走
/// <see cref="Process.Kill"/>，且强杀前用启动时间复核 PID 重用。不引入任何 P/Invoke、
/// 进程启动、命令解释器或 shell。访问拒绝、进程已退出、窗口消失等竞态安全收敛。
/// </summary>
public sealed class DiagnosticAppWindowManager : IAppWindowManager
{
    public bool HasMainWindow(int processId)
    {
        using var process = Open(processId);
        if (process is null)
        {
            return false;
        }

        try
        {
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    public bool RequestClose(int processId)
    {
        using var process = Open(processId);
        if (process is null)
        {
            return false;
        }

        try
        {
            // WM_CLOSE：优雅关闭；返回 false 表示无主窗口/已在关闭/投递失败。
            return process.CloseMainWindow();
        }
        catch
        {
            return false;
        }
    }

    public bool HasExited(int processId)
    {
        using var process = Open(processId);
        if (process is null)
        {
            return true; // 进程不存在视为已退出。
        }

        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        using var process = Open(processId);
        if (process is null)
        {
            return true;
        }

        try
        {
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch
        {
            return HasExited(processId);
        }
    }

    public ForceKillResult ForceKill(int processId, DateTimeOffset expectedStartTimeUtc)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new ForceKillResult { Status = ForceKillStatus.AlreadyExited };
        }

        using (process)
        {
            // 强杀前复核：重新读取当前 PID 的启动时间，与解析时一致才允许 Kill（PID 重用保护）。
            var freshStart = ReadStartTime(process);
            if (freshStart == default || freshStart != expectedStartTimeUtc)
            {
                return new ForceKillResult
                {
                    Status = ForceKillStatus.PidReuseDetected,
                    Details = "The process start time no longer matches the resolved target."
                };
            }

            try
            {
                if (process.HasExited)
                {
                    return new ForceKillResult { Status = ForceKillStatus.AlreadyExited };
                }

                process.Kill();
                process.WaitForExit(1000);
                return new ForceKillResult { Status = ForceKillStatus.Killed };
            }
            catch (Win32Exception exception)
            {
                return new ForceKillResult
                {
                    Status = ForceKillStatus.AccessDenied,
                    Details = $"Access denied (0x{exception.NativeErrorCode:X})."
                };
            }
            catch (InvalidOperationException)
            {
                return new ForceKillResult { Status = ForceKillStatus.AlreadyExited };
            }
            catch (NotSupportedException)
            {
                return new ForceKillResult { Status = ForceKillStatus.Failed };
            }
        }
    }

    private static Process? Open(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
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
}
