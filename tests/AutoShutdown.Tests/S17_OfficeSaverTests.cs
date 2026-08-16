using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Office;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S17 OfficeDetector/OfficeDocumentSaver 服务层单元测试。全部通过 FakeOfficeAutomation
/// 替身验证，绝不启动或操纵真实 Word/Excel/PowerPoint。覆盖成功、无目标、无路径、单项失败、
/// 超时、取消、异常隔离、多应用隔离与脱敏。
/// </summary>
public sealed class S17_OfficeSaverTests
{
    [Fact]
    public async Task SaveAll_Success_SavesEachAvailableApp()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Word, OfficeApplicationKind.Excel],
            OnSave = (app, _) => new OfficeApplicationSaveResult
            {
                Application = app,
                Status = OfficeAppStatus.Success,
                SavedCount = 2
            }
        };
        var saver = new OfficeDocumentSaver(automation);

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(string.Empty, report.Summary);
        Assert.Equal([OfficeApplicationKind.Word, OfficeApplicationKind.Excel], automation.SaveCalls);
    }

    [Fact]
    public async Task SaveAll_NoTarget_ReportsSuccess()
    {
        var automation = new FakeOfficeAutomation { Detected = [] };
        var saver = new OfficeDocumentSaver(automation);

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Empty(report.Applications);
        Assert.Empty(automation.SaveCalls);
    }

    [Fact]
    public async Task SaveAll_NoPath_IsPartialFailure()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Word],
            OnSave = (app, _) => new OfficeApplicationSaveResult
            {
                Application = app,
                Status = OfficeAppStatus.PartialFailure,
                NoPathCount = 1
            }
        };
        var saver = new OfficeDocumentSaver(automation);

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal("Word: 1 no-path, 0 failed", report.Summary);
    }

    [Fact]
    public async Task SaveAll_SingleFailure_IsPartialFailure()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Excel],
            OnSave = (app, _) => new OfficeApplicationSaveResult
            {
                Application = app,
                Status = OfficeAppStatus.PartialFailure,
                FailedCount = 1
            }
        };
        var saver = new OfficeDocumentSaver(automation);

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal("Excel: 0 no-path, 1 failed", report.Summary);
    }

    [Fact]
    public async Task SaveAll_Timeout_IsIsolatedTimedOut()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Word],
            OnSave = (app, ct) =>
            {
                ct.WaitHandle.WaitOne();
                ct.ThrowIfCancellationRequested();
                return new OfficeApplicationSaveResult
                {
                    Application = app,
                    Status = OfficeAppStatus.Success
                };
            }
        };
        var saver = new OfficeDocumentSaver(automation, TimeSpan.FromMilliseconds(50));

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal("Word: timed out", report.Summary);
    }

    [Fact]
    public async Task SaveAll_Cancel_PropagatesWithoutSideEffects()
    {
        var automation = new FakeOfficeAutomation { Detected = [OfficeApplicationKind.Word] };
        var saver = new OfficeDocumentSaver(automation);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => saver.SaveAllAsync(cts.Token));

        Assert.Empty(automation.SaveCalls);
    }

    [Fact]
    public async Task SaveAll_Exception_IsIsolatedAsComUnavailable()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Word],
            OnSave = (_, _) => throw new InvalidOperationException("boom")
        };
        var saver = new OfficeDocumentSaver(automation);

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal("Word: COM unavailable", report.Summary);
    }

    [Fact]
    public async Task SaveAll_MultiApp_SingleFailure_DoesNotStopRemainingApps()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Word, OfficeApplicationKind.Excel, OfficeApplicationKind.PowerPoint],
            OnSave = (app, _) => app == OfficeApplicationKind.Excel
                ? throw new InvalidOperationException("boom")
                : new OfficeApplicationSaveResult { Application = app, Status = OfficeAppStatus.Success, SavedCount = 1 }
        };
        var saver = new OfficeDocumentSaver(automation);

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(
            [OfficeApplicationKind.Word, OfficeApplicationKind.Excel, OfficeApplicationKind.PowerPoint],
            automation.SaveCalls);
        Assert.Equal("Excel: COM unavailable", report.Summary);
    }

    [Fact]
    public async Task SaveAll_Summary_NeverLeaksFileNamesOrPathsOrControlChars()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Word],
            OnSave = (app, _) => new OfficeApplicationSaveResult
            {
                Application = app,
                Status = OfficeAppStatus.PartialFailure,
                NoPathCount = 3,
                FailedCount = 2
            }
        };
        var saver = new OfficeDocumentSaver(automation);

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.Equal("Word: 3 no-path, 2 failed", report.Summary);
        Assert.DoesNotContain("\\", report.Summary);
        Assert.DoesNotContain("/", report.Summary);
        Assert.DoesNotContain(".docx", report.Summary);
        Assert.All(report.Summary, character => Assert.False(char.IsControl(character)));
    }

    [Fact]
    public void Detector_ReturnsAvailableApplications()
    {
        var automation = new FakeOfficeAutomation
        {
            Detected = [OfficeApplicationKind.Word, OfficeApplicationKind.PowerPoint]
        };
        var detector = new OfficeDetector(automation);

        Assert.Equal([OfficeApplicationKind.Word, OfficeApplicationKind.PowerPoint], detector.DetectAvailable());
    }

    private sealed class FakeOfficeAutomation : IOfficeAutomation
    {
        public List<OfficeApplicationKind> Detected { get; init; } = [];

        public Func<OfficeApplicationKind, CancellationToken, OfficeApplicationSaveResult> OnSave { get; init; }
            = (app, _) => new OfficeApplicationSaveResult { Application = app, Status = OfficeAppStatus.Success };

        public List<OfficeApplicationKind> SaveCalls { get; } = [];

        public IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications() => Detected;

        public OfficeApplicationSaveResult SaveOpenDocuments(
            OfficeApplicationKind application,
            CancellationToken cancellationToken)
        {
            SaveCalls.Add(application);
            return OnSave(application, cancellationToken);
        }
    }
}
