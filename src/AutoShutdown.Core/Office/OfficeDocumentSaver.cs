using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Office;

/// <summary>
/// Office 文档保存编排（S17）。对每个可用应用串行执行保存，逐应用异常隔离，
/// 每应用独立超时；单应用失败/超时不拖垮整体。取消（CancellationToken）直接传播，
/// 不产生副作用。摘要只含应用名与计数，不含文件名/路径/文档内容。
/// </summary>
public sealed class OfficeDocumentSaver
{
    private static readonly TimeSpan DefaultPerAppTimeout = TimeSpan.FromSeconds(15);

    private readonly OfficeDetector _detector;
    private readonly IOfficeAutomation _automation;
    private readonly TimeSpan _perAppTimeout;

    public OfficeDocumentSaver(IOfficeAutomation automation, TimeSpan? perAppTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _detector = new OfficeDetector(automation);
        _automation = automation;
        _perAppTimeout = perAppTimeout ?? DefaultPerAppTimeout;
    }

    public async Task<OfficeSaveReport> SaveAllAsync(CancellationToken cancellationToken)
    {
        var applications = _detector.DetectAvailable();
        if (applications.Count == 0)
        {
            return new OfficeSaveReport();
        }

        var results = new List<OfficeApplicationSaveResult>(applications.Count);
        foreach (var application in applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await SaveApplicationAsync(application, cancellationToken).ConfigureAwait(false));
        }

        return new OfficeSaveReport
        {
            Applications = results,
            Summary = BuildSummary(results)
        };
    }

    private async Task<OfficeApplicationSaveResult> SaveApplicationAsync(
        OfficeApplicationKind application,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_perAppTimeout);

        try
        {
            return await Task.Run(
                () => _automation.SaveOpenDocuments(application, timeoutCts.Token),
                timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OfficeHelperCleanupFailedException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消期间的清理失败：仍以纯 OCE 传播（D4 要求 2），绝不转 NotDetected。
            throw new OperationCanceledException("The operation was cancelled.", cancellationToken);
        }
        catch (OfficeHelperCleanupFailedException)
        {
            // 内部逐应用硬超时期间无法确认 Helper 退出：安全故障，向上传播（D4 要求 3），
            // 绝不降级为 TimedOut/NotDetected 进入默认 Continue。
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消（清理成功）：传播 OCE，绝不转 NotDetected（D4 要求 2）。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 内部逐应用超时（清理成功）：正常 TimedOut（可继续）。
            return new OfficeApplicationSaveResult
            {
                Application = application,
                Status = OfficeAppStatus.TimedOut
            };
        }
        catch (Exception)
        {
            // 逐应用异常隔离：单个应用 COM 失败不得使进程失控，其余应用继续。
            return new OfficeApplicationSaveResult
            {
                Application = application,
                Status = OfficeAppStatus.NotDetected
            };
        }
    }

    private static string BuildSummary(IReadOnlyList<OfficeApplicationSaveResult> results)
    {
        if (results.All(result => result.Status == OfficeAppStatus.Success))
        {
            return string.Empty;
        }

        return string.Join("; ", results
            .Where(result => result.Status != OfficeAppStatus.Success)
            .Select(Describe));
    }

    private static string Describe(OfficeApplicationSaveResult result)
    {
        var app = result.Application switch
        {
            OfficeApplicationKind.Word => "Word",
            OfficeApplicationKind.Excel => "Excel",
            OfficeApplicationKind.PowerPoint => "PowerPoint",
            _ => result.Application.ToString()
        };

        return result.Status switch
        {
            OfficeAppStatus.NotDetected => $"{app}: COM unavailable",
            OfficeAppStatus.TimedOut => $"{app}: timed out",
            OfficeAppStatus.PartialFailure => $"{app}: {result.NoPathCount} no-path, {result.FailedCount} failed",
            _ => $"{app}: unexpected status"
        };
    }
}
