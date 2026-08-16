using System.Diagnostics;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Office;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// 真实 Office 自动保存编排（S17 独立验收 D2）。探测可用应用后，为每个应用启动
/// 独立辅助进程执行 ROT 附加与保存；父进程按单应用期限硬终止辅助进程整棵进程树，
/// 实现真正的逐应用硬超时（辅助进程绝不新建实例、绝不 Quit，关闭归 S18）。
/// 进程启动收敛于 <see cref="IOfficeSaveHelperLauncher"/>（唯一进程启动网关）。
/// 真机路径，S17 自动化测试注入替身启动器，不启动真实进程。
/// </summary>
public sealed class ComOfficeAutomation : IOfficeAutomation
{
    private static readonly (OfficeApplicationKind Kind, string ProgId, string ProcessName)[] Programs =
    [
        (OfficeApplicationKind.Word, "Word.Application", "WINWORD"),
        (OfficeApplicationKind.Excel, "Excel.Application", "EXCEL"),
        (OfficeApplicationKind.PowerPoint, "PowerPoint.Application", "POWERPNT"),
    ];

    private static readonly TimeSpan KillConfirmTimeout = TimeSpan.FromSeconds(5);
    private const int PollIntervalMilliseconds = 200;

    private readonly IOfficeSaveHelperLauncher _launcher;

    public ComOfficeAutomation(IOfficeSaveHelperLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
    }

    public IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications()
    {
        var available = new List<OfficeApplicationKind>(Programs.Length);
        foreach (var (kind, progId, processName) in Programs)
        {
            if (IsProgIdRegistered(progId) && IsProcessRunning(processName))
            {
                available.Add(kind);
            }
        }

        return available;
    }

    public OfficeApplicationSaveResult SaveOpenDocuments(
        OfficeApplicationKind application,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();

        IOfficeSaveHelperProcess? process = null;
        try
        {
            try
            {
                process = _launcher.Launch(application, correlationId);
            }
            catch (Exception)
            {
                // 启动失败（路径校验失败/进程不存在/启动异常）→ 可诊断失败，不阻断整体流程。
                return NotDetected(application);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.WaitForExit(PollIntervalMilliseconds))
                {
                    return ParseResult(process.ReadStandardOutput(), application);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 硬超时或上层取消：先终止辅助进程整棵进程树并确认退出，再传播取消，
            // 绝不遗留仍在运行的辅助进程（不丢弃任务）、绝不终止用户 Office。
            if (process is not null)
            {
                process.KillTree();
                process.WaitForExitAfterKill(KillConfirmTimeout);
            }

            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static OfficeApplicationSaveResult ParseResult(string? stdout, OfficeApplicationKind application)
    {
        if (OfficeSaveHelperProtocol.TryParse(stdout, out var result))
        {
            return result with { Application = application };
        }

        // 输出缺失/非法（如辅助进程看门狗自终止）→ 回退为超时，绝不信任未知内容。
        return new OfficeApplicationSaveResult
        {
            Application = application,
            Status = OfficeAppStatus.TimedOut
        };
    }

    private static OfficeApplicationSaveResult NotDetected(OfficeApplicationKind application)
        => new() { Application = application, Status = OfficeAppStatus.NotDetected };

    private static bool IsProgIdRegistered(string progId)
    {
        try
        {
            return Type.GetTypeFromProgID(progId) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
