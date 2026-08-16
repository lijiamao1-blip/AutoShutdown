using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Office;

namespace AutoShutdown.App.Infrastructure.Office;

/// <summary>
/// 真实 Office COM 自动化（S17）。只对「已安装且正在运行」的 Office 应用做 COM 附加保存：
/// 通过进程探测限定目标，绝不新建隐藏进程（从根上避免遗留进程），也绝不 Quit 用户已打开的
/// Office 应用（关闭归 S18）。在明确 STA 边界内执行，逐对象释放 COM 引用。此实现为真机路径，
/// S17 自动化测试不调用它。
/// </summary>
public sealed class ComOfficeAutomation : IOfficeAutomation
{
    private static readonly (OfficeApplicationKind Kind, string ProgId, string ProcessName)[] Programs =
    [
        (OfficeApplicationKind.Word, "Word.Application", "WINWORD"),
        (OfficeApplicationKind.Excel, "Excel.Application", "EXCEL"),
        (OfficeApplicationKind.PowerPoint, "PowerPoint.Application", "POWERPNT"),
    ];

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

    private static OfficeApplicationSaveResult SaveOnStaThread(
        OfficeApplicationKind application,
        CancellationToken cancellationToken)
    {
        var progId = ResolveProgId(application);
        if (progId is null)
        {
            return NotDetected(application);
        }

        var type = Type.GetTypeFromProgID(progId);
        if (type is null)
        {
            return NotDetected(application);
        }

        object? applicationObject = null;
        object? documentsObject = null;

        try
        {
            // 附加到正在运行的实例（Detect 已确认进程存在）；不新建隐藏进程。
            applicationObject = Activator.CreateInstance(type);
            if (applicationObject is null)
            {
                return NotDetected(application);
            }

            dynamic app = applicationObject;
            documentsObject = GetDocuments(app, application);
            dynamic documents = documentsObject;

            var saved = 0;
            var noPath = 0;
            var failed = 0;
            var count = (int)documents.Count;
            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                object? documentObject = null;
                try
                {
                    documentObject = documents.Item(index);
                    dynamic document = documentObject;
                    var path = document.Path as string;
                    if (string.IsNullOrEmpty(path))
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
                finally
                {
                    Release(documentObject);
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
            // 仅释放 COM 引用；绝不 Quit（关闭用户 Office 应用归 S18）。
            Release(documentsObject);
            Release(applicationObject);
        }
    }

    private static object GetDocuments(dynamic application, OfficeApplicationKind applicationKind)
        => applicationKind switch
        {
            OfficeApplicationKind.Word => application.Documents,
            OfficeApplicationKind.Excel => application.Workbooks,
            OfficeApplicationKind.PowerPoint => application.Presentations,
            _ => throw new InvalidOperationException($"Unknown Office application: {applicationKind}.")
        };

    private static OfficeApplicationSaveResult NotDetected(OfficeApplicationKind application)
        => new() { Application = application, Status = OfficeAppStatus.NotDetected };

    private static string? ResolveProgId(OfficeApplicationKind application)
    {
        foreach (var (kind, progId, _) in Programs)
        {
            if (kind == application)
            {
                return progId;
            }
        }

        return null;
    }

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

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
