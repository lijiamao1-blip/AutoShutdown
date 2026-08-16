using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Office;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S17 OfficeSaveAction 动作层单元测试。验证动作契约（名称/策略）、上下文校验、
/// 以及报告到 PrePipelineActionResult 的映射（成功清空错误文本、失败携带脱敏摘要）。
/// 全部通过 FakeOfficeAutomation 替身验证，绝不触碰真实 Office。
/// </summary>
public sealed class S17_OfficeSaveActionTests
{
    [Fact]
    public void Defaults_NameIsOfficeSave_AndPolicyIsContinue()
    {
        var action = new OfficeSaveAction(new FakeOfficeAutomation(new OfficeSaveReport()));

        Assert.Equal("OfficeSave", action.Name);
        Assert.Equal(FailurePolicy.Continue, action.FailurePolicy);
    }

    [Fact]
    public void BlockPolicyVariant_ExposesBlock()
    {
        var action = new OfficeSaveAction(new FakeOfficeAutomation(new OfficeSaveReport()), failurePolicy: FailurePolicy.Block);

        Assert.Equal(FailurePolicy.Block, action.FailurePolicy);
    }

    [Fact]
    public async Task Success_MapsToSucceededWithEmptyError()
    {
        var automation = new FakeOfficeAutomation(new OfficeSaveReport());
        var action = new OfficeSaveAction(automation);

        var result = await action.ExecuteAsync(Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.ErrorMessage);
    }

    [Fact]
    public async Task Failure_MapsSummaryToErrorMessage()
    {
        var automation = new FakeOfficeAutomation(new OfficeSaveReport
        {
            Applications =
            [
                new OfficeApplicationSaveResult
                {
                    Application = OfficeApplicationKind.Word,
                    Status = OfficeAppStatus.PartialFailure,
                    NoPathCount = 1
                }
            ],
            Summary = "Word: 1 no-path, 0 failed"
        });
        var action = new OfficeSaveAction(automation);

        var result = await action.ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Word: 1 no-path, 0 failed", result.ErrorMessage);
    }

    [Fact]
    public void NullAutomation_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OfficeSaveAction(null!));
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        var action = new OfficeSaveAction(new FakeOfficeAutomation(new OfficeSaveReport()));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static PrePipelineContext Context() => new()
    {
        InstanceId = Guid.NewGuid(),
        SourceTaskId = Guid.NewGuid(),
        Action = PowerAction.Shutdown,
        ScheduledFireTime = DateTimeOffset.UtcNow
    };

    private sealed class FakeOfficeAutomation : IOfficeAutomation
    {
        private readonly OfficeSaveReport _report;

        public FakeOfficeAutomation(OfficeSaveReport report) => _report = report;

        public IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications()
            => _report.Applications
                .Select(application => application.Application)
                .ToList();

        public OfficeApplicationSaveResult SaveOpenDocuments(
            OfficeApplicationKind application,
            CancellationToken cancellationToken)
            => _report.Applications.First(result => result.Application == application);
    }
}
