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
                    // 消除取消竞态：Helper 已退出后、解析 stdout 前再次检查取消令牌。
                    // 若取消已到达则走取消清理路径，绝不返回可能掩盖取消的正常结果。
                    cancellationToken.ThrowIfCancellationRequested();
                    return ParseResult(process.ReadStandardOutput(), application);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 硬超时或上层取消：先清理辅助进程（终止整棵树 + 有界等待确认退出），再传播取消。
            // 清理无论成功与否、是否抛异常，外部取消都仍以 OCE 传播：被上层识别为取消/TimedOut，
            // 绝不被转换为 NotDetected 等普通结果；也绝不遗留仍在运行的辅助进程、绝不终止用户 Office。
            try
            {
                CleanupAfterCancellation(process);
            }
            catch (Exception)
            {
                // 清理异常（含无法确认退出抛出的 HelperCleanupFailedException）不得覆盖原始取消：
                // 仍以 OCE 传播（明确失败路径已在 CleanupAfterCancellation 边界标记，见 D3）。
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

    /// <summary>
    /// 取消/超时后的 Helper 清理（S17 独立验收 D3，收敛为可测试逻辑）：仅针对已启动的
    /// 辅助进程，终止整棵进程树并以有界等待确认退出；绝不触碰用户 Office 进程。
    /// 无法确认退出（含 Kill/等待确认抛异常）抛 <see cref="HelperCleanupFailedException"/>
    /// 作为明确失败路径——绝不静默忽略确认结果，也绝不伪装成成功或正常结果。
    /// </summary>
    internal static void CleanupAfterCancellation(IOfficeSaveHelperProcess? process)
    {
        if (process is null)
        {
            // 未启动辅助进程：无可清理目标，视为完成。
            return;
        }

        try
        {
            process.KillTree();
        }
        catch (Exception exception)
        {
            throw new HelperCleanupFailedException("Failed to terminate the Office save helper.", exception);
        }

        var exitConfirmed = false;
        try
        {
            exitConfirmed = process.WaitForExitAfterKill(KillConfirmTimeout);
        }
        catch (Exception exception)
        {
            throw new HelperCleanupFailedException("Failed to confirm Office save helper exit.", exception);
        }

        if (!exitConfirmed)
        {
            // 无法确认退出：明确失败路径，绝不静默忽略。
            throw new HelperCleanupFailedException("The Office save helper did not confirm exit after kill.");
        }
    }

    /// <summary>辅助进程清理失败（无法终止或无法确认退出）的明确失败标记。</summary>
    internal sealed class HelperCleanupFailedException : Exception
    {
        public HelperCleanupFailedException(string message)
            : base(message)
        {
        }

        public HelperCleanupFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
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
