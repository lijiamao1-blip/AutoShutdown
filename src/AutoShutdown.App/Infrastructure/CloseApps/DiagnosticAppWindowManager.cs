using System.ComponentModel;
using System.Diagnostics;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;

namespace AutoShutdown.App.Infrastructure.CloseApps;

/// <summary>
/// 基于 System.Diagnostics.Process 的真实窗口/进程关闭（S18）。优雅关闭走托管
/// <see cref="Process.CloseMainWindow"/>（等价 WM_CLOSE 到主窗口）；强杀走
/// <see cref="Process.Kill"/>，且强杀前用启动时间复核 PID 重用、强杀后用有界窗口
/// 确认退出（未确认绝不返回成功，D1-4）。退出状态为三态（Unknown/Exited/Running），
/// 查询异常返回 Unknown（D1-3，fail-closed）。不引入任何 P/Invoke、进程启动、
/// 命令解释器或 shell。
/// </summary>
public sealed class DiagnosticAppWindowManager : IAppWindowManager
{
    private const int WaitPollIntervalMs = 100;
    private const int KillConfirmationTimeoutMs = 3000;

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

    public ProcessExitStatus GetExitStatus(int processId)
    {
        using var process = Open(processId);
        if (process is null)
        {
            // 进程不存在（已退出/从未存在）：确定已退出，不是「无法确认」。
            return ProcessExitStatus.Exited;
        }

        try
        {
            return process.HasExited
                ? ProcessExitStatus.Exited
                : ProcessExitStatus.Running;
        }
        catch
        {
            // 查询失败：无法确认。绝不把「无法确认」伪装成「已退出」（D1-3）。
            return ProcessExitStatus.Unknown;
        }
    }

    public ProcessWaitResult WaitForExit(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Open(processId);
        if (process is null)
        {
            return ProcessWaitResult.Exited; // 进程不存在：已退出。
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool exited;
            try
            {
                exited = process.WaitForExit(WaitPollIntervalMs);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 等待异常：复核退出状态，完整传播三态，绝不把「无法确认」折叠成普通超时（D2）。
                return GetExitStatus(processId) switch
                {
                    ProcessExitStatus.Exited => ProcessWaitResult.Exited,
                    ProcessExitStatus.Unknown => ProcessWaitResult.Unknown,
                    _ => ProcessWaitResult.TimedOut
                };
            }

            if (exited)
            {
                return ProcessWaitResult.Exited;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return ProcessWaitResult.TimedOut; // 确定仍在运行且达到期限。
            }
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
                return ConfirmExit(process);
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

    /// <summary>
    /// Kill 发出后的退出确认（D1-4）。有界等待；未退出或确认查询失败一律返回
    /// <see cref="ForceKillStatus.ExitNotConfirmed"/>，绝不把「未确认」当成成功。
    /// </summary>
    private static ForceKillResult ConfirmExit(Process process)
    {
        try
        {
            return process.WaitForExit(KillConfirmationTimeoutMs)
                ? new ForceKillResult { Status = ForceKillStatus.Killed }
                : new ForceKillResult
                {
                    Status = ForceKillStatus.ExitNotConfirmed,
                    Details = "Kill was issued but the process did not confirm exit within the bounded window."
                };
        }
        catch
        {
            return new ForceKillResult
            {
                Status = ForceKillStatus.ExitNotConfirmed,
                Details = "Exit confirmation failed after Kill; the process may still be running."
            };
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
