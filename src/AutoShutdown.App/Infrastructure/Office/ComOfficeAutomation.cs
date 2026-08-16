using System.Diagnostics;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Office;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// 真实 Office 自动保存编排（S17 独立验收修复）。只对「已安装且正在运行」的 Office 应用，
/// 通过 <see cref="IOfficeComGateway"/> 附加 Running Object Table 中已运行实例并保存；
/// 绝不新建实例、绝不 Quit（关闭归 S18）。逐文档/逐应用异常隔离，全部取得的 COM 包装
/// 在成功、异常与取消路径均释放。此实现为真机路径，S17 自动化测试不调用它。
/// </summary>
public sealed class ComOfficeAutomation : IOfficeAutomation
{
    private static readonly (OfficeApplicationKind Kind, string ProgId, string ProcessName)[] Programs =
    [
        (OfficeApplicationKind.Word, "Word.Application", "WINWORD"),
        (OfficeApplicationKind.Excel, "Excel.Application", "EXCEL"),
        (OfficeApplicationKind.PowerPoint, "PowerPoint.Application", "POWERPNT"),
    ];

    private readonly IOfficeComGateway _gateway;

    public ComOfficeAutomation(IOfficeComGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
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
        return StaThreadRunner.Run(() => SaveOnStaThread(application, cancellationToken));
    }

    private OfficeApplicationSaveResult SaveOnStaThread(
        OfficeApplicationKind application,
        CancellationToken cancellationToken)
    {
        IOfficeComApplication? officeApplication = null;
        try
        {
            // 仅附加 ROT 中已运行实例；网关无创建路径，进程探测与附加间竞态安全失败为 null。
            officeApplication = _gateway.TryAttach(application);
            if (officeApplication is null)
            {
                return NotDetected(application);
            }

            IReadOnlyList<IOfficeComDocument> documents;
            try
            {
                documents = officeApplication.GetOpenDocuments();
            }
            catch (Exception)
            {
                return NotDetected(application);
            }

            var saved = 0;
            var noPath = 0;
            var failed = 0;
            try
            {
                foreach (var document in documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!document.HasPath)
                        {
                            noPath++;
                        }
                        else
                        {
                            document.Save();
                            saved++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }
            }
            finally
            {
                // 成功 / 异常 / 取消路径均释放全部取得的文档包装。
                foreach (var document in documents)
                {
                    document.Dispose();
                }
            }

            return new OfficeApplicationSaveResult
            {
                Application = application,
                Status = noPath == 0 && failed == 0 ? OfficeAppStatus.Success : OfficeAppStatus.PartialFailure,
                SavedCount = saved,
                NoPathCount = noPath,
                FailedCount = failed
            };
        }
        finally
        {
            // 仅释放本程序取得的 COM 引用；绝不 Quit。
            officeApplication?.Dispose();
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
