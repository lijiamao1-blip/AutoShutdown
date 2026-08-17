using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Rtc;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C4：一次性 RTC 唤醒能力状态分区（设置页，只读）。能力探测结果诚实展示：
/// 支持（区分唤醒范围）/不支持/状态未知（fail-closed）/查询异常一律明确显示失败原因。
/// 查询仅能力探测，不设置任何真实唤醒定时器。全部纯内存假服务。
/// </summary>
public sealed class S21_RtcStatusSectionViewModelTests
{
    [Fact]
    public async Task Refresh_WhenSupportedSuspend_ShowsSuspendOnlyScope()
    {
        var service = FakeRtcWakeService.Capability(new RtcWakeCapabilityResult
        {
            Status = RtcWakeCapabilityStatus.Supported,
            WakeScope = RtcWakeScope.Suspend,
            Reason = "waitable timer"
        });
        var viewModel = new RtcStatusSectionViewModel(service);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("支持（仅睡眠/休眠后自动唤醒）", viewModel.StatusText);
        Assert.Equal("waitable timer", viewModel.DetailText);
        Assert.True(viewModel.IsSupported);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Refresh_WhenSupportedSuspendAndPowerOff_ShowsFullScope()
    {
        var service = FakeRtcWakeService.Capability(new RtcWakeCapabilityResult
        {
            Status = RtcWakeCapabilityStatus.Supported,
            WakeScope = RtcWakeScope.SuspendAndPowerOff,
            Reason = "RTC alarm"
        });
        var viewModel = new RtcStatusSectionViewModel(service);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("支持（含完全关机后自动唤醒）", viewModel.StatusText);
        Assert.True(viewModel.IsSupported);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Refresh_WhenNotSupported_ShowsNotSupported()
    {
        var service = FakeRtcWakeService.Capability(new RtcWakeCapabilityResult
        {
            Status = RtcWakeCapabilityStatus.NotSupported,
            Reason = "firmware lacks RTC wake"
        });
        var viewModel = new RtcStatusSectionViewModel(service);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("不支持", viewModel.StatusText);
        Assert.Equal("firmware lacks RTC wake", viewModel.DetailText);
        Assert.False(viewModel.IsSupported);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Refresh_WhenUnknown_FailClosed()
    {
        var service = FakeRtcWakeService.Capability(new RtcWakeCapabilityResult
        {
            Status = RtcWakeCapabilityStatus.Unknown,
            Reason = "no probe"
        });
        var viewModel = new RtcStatusSectionViewModel(service);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("状态未知", viewModel.StatusText);
        Assert.False(viewModel.IsSupported);
        Assert.True(viewModel.HasError);
        Assert.Contains("无法确定 RTC 唤醒能力", viewModel.ErrorText);
    }

    [Fact]
    public async Task Refresh_WhenCapabilityThrows_ShowsFailure()
    {
        var service = FakeRtcWakeService.Throwing(new InvalidOperationException("probe failed"));
        var viewModel = new RtcStatusSectionViewModel(service);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("查询失败", viewModel.StatusText);
        Assert.False(viewModel.IsSupported);
        Assert.True(viewModel.HasError);
        Assert.Contains("RTC 唤醒能力查询失败", viewModel.ErrorText);
        Assert.Contains("probe failed", viewModel.ErrorText);
    }

    [Fact]
    public async Task Refresh_WhenCancelled_Rethrows()
    {
        var service = FakeRtcWakeService.Throwing(new OperationCanceledException());
        var viewModel = new RtcStatusSectionViewModel(service);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => viewModel.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_SetsBusyAroundProbe()
    {
        var tcs = new TaskCompletionSource<RtcWakeCapabilityResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = FakeRtcWakeService.Probe(_ => tcs.Task);
        var viewModel = new RtcStatusSectionViewModel(service);

        var refreshTask = viewModel.RefreshAsync(CancellationToken.None);
        Assert.True(viewModel.IsBusy); // 探测进行中即忙（阻止重复查询）

        tcs.SetResult(new RtcWakeCapabilityResult
        {
            Status = RtcWakeCapabilityStatus.Supported,
            WakeScope = RtcWakeScope.Suspend
        });
        await refreshTask;

        Assert.False(viewModel.IsBusy);
        Assert.Equal("支持（仅睡眠/休眠后自动唤醒）", viewModel.StatusText);
    }

    private sealed class FakeRtcWakeService : IRtcWakeService
    {
        private readonly Func<CancellationToken, Task<RtcWakeCapabilityResult>> _capability;

        private FakeRtcWakeService(Func<CancellationToken, Task<RtcWakeCapabilityResult>> capability)
            => _capability = capability;

        public static FakeRtcWakeService Probe(Func<CancellationToken, Task<RtcWakeCapabilityResult>> probe)
            => new(probe);

        public static FakeRtcWakeService Capability(RtcWakeCapabilityResult result)
            => new(_ => Task.FromResult(result));

        public static FakeRtcWakeService Throwing(Exception exception)
            => new(_ => throw exception);

        public Task<RtcWakeCapabilityResult> GetCapabilityAsync(CancellationToken cancellationToken)
            => _capability(cancellationToken);

        public Task<RtcWakeSetResult> SetWakeAsync(DateTimeOffset wakeTimeUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RtcWakeClearResult> ClearAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
