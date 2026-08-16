using System.Diagnostics;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// System.Diagnostics.Process 的辅助进程包装（S17 独立验收 D2）。将终止整棵树、
/// 等待确认退出与固定结果读取收敛为最小句柄，供编排层在硬超时/取消时安全清理。
/// </summary>
public sealed class OfficeSaveHelperProcess : IOfficeSaveHelperProcess
{
    private readonly Process _process;

    public OfficeSaveHelperProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
    }

    public bool HasExited => _process.HasExited;

    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

    public string ReadStandardOutput()
    {
        // 读取固定结果；顺带清空 stderr（辅助进程 stderr 仅固定错误码，量极小）防缓冲占满。
        _ = _process.StandardError.ReadToEnd();
        return _process.StandardOutput.ReadToEnd();
    }

    public void KillTree()
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            // 终止整棵进程树；仅辅助进程，绝不终止用户 Office 进程。
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 竞态：等待与终止之间进程已退出。
        }
    }

    public bool WaitForExitAfterKill(TimeSpan timeout)
        => _process.WaitForExit((int)timeout.TotalMilliseconds);

    public void Dispose() => _process.Dispose();
}
