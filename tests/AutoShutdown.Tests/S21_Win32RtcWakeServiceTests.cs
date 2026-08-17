using AutoShutdown.App.Infrastructure.Rtc;
using AutoShutdown.Core.Rtc;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C2：Win32RtcWakeService 状态映射（经替身 IRtcWakeNativeApi，绝不触碰真实 timer）。
/// 能力诚实声明（仅可唤醒睡眠/休眠）；权限不足 → NotAuthorized；系统拒绝 → FirmwareRejected；
/// 取消传播；清除失败明确上报 CleanupFailed。
/// </summary>
public sealed class S21_Win32RtcWakeServiceTests
{
    private static readonly DateTimeOffset WakeTime =
        new(2024, 1, 16, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetCapability_ReportsSuspendWakeOnly_WithHonestReason()
    {
        var service = new Win32RtcWakeService(new FakeNativeApi());

        var result = await service.GetCapabilityAsync(CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(RtcWakeScope.Suspend, result.WakeScope);
        // 诚实声明边界：不伪装支持完全关机（S5）唤醒。
        Assert.Contains("S5", result.Reason);
    }

    [Fact]
    public async Task SetWake_Success_ReturnsSuccessAndClearClosesHandle()
    {
        var native = new FakeNativeApi();
        var service = new Win32RtcWakeService(native);

        var set = await service.SetWakeAsync(WakeTime, CancellationToken.None);

        Assert.True(set.Succeeded);
        Assert.Equal(1, native.CreateCalls);
        Assert.Equal(1, native.SetWakeCalls);
        Assert.Equal(WakeTime.ToFileTime(), native.SetWakeDueTime);

        var clear = await service.ClearAsync(CancellationToken.None);
        Assert.True(clear.Succeeded);
        Assert.Equal(1, native.CancelCalls);
        Assert.Equal(1, native.CloseCalls);
        Assert.Contains(native.Handle, native.CancelledHandles);
        Assert.Contains(native.Handle, native.ClosedHandles);
    }

    [Fact]
    public async Task SetWake_WhenCreateFails_ReturnsFirmwareRejected()
    {
        var native = new FakeNativeApi { NextHandle = IntPtr.Zero };
        var service = new Win32RtcWakeService(native);

        var set = await service.SetWakeAsync(WakeTime, CancellationToken.None);

        Assert.Equal(RtcWakeSetStatus.FirmwareRejected, set.Status);
        Assert.False(set.Succeeded);
        Assert.Equal(0, native.SetWakeCalls);
    }

    [Fact]
    public async Task SetWake_WhenNativeRejects_ReturnsFirmwareRejected()
    {
        var native = new FakeNativeApi { SetWakeResult = new NativeCallResult(false, 87) };
        var service = new Win32RtcWakeService(native);

        var set = await service.SetWakeAsync(WakeTime, CancellationToken.None);

        Assert.Equal(RtcWakeSetStatus.FirmwareRejected, set.Status);
        Assert.Contains("87", set.Message);
        // 失败时关闭已创建的句柄，不残留。
        Assert.Contains(native.Handle, native.ClosedHandles);
    }

    [Theory]
    [InlineData(1314)] // ERROR_PRIVILEGE_NOT_HELD
    [InlineData(5)]    // ERROR_ACCESS_DENIED
    public async Task SetWake_WhenPermissionInsufficient_ReturnsNotAuthorized(int errorCode)
    {
        var native = new FakeNativeApi { SetWakeResult = new NativeCallResult(false, errorCode) };
        var service = new Win32RtcWakeService(native);

        var set = await service.SetWakeAsync(WakeTime, CancellationToken.None);

        Assert.Equal(RtcWakeSetStatus.NotAuthorized, set.Status);
        Assert.False(set.Succeeded);
    }

    [Fact]
    public async Task Clear_WhenNoTimer_ReturnsSuccessWithoutNativeCalls()
    {
        var native = new FakeNativeApi();
        var service = new Win32RtcWakeService(native);

        var clear = await service.ClearAsync(CancellationToken.None);

        Assert.True(clear.Succeeded);
        Assert.Equal(0, native.CancelCalls);
        Assert.Equal(0, native.CloseCalls);
    }

    [Fact]
    public async Task Clear_WhenCancelFails_ReturnsCleanupFailed()
    {
        var native = new FakeNativeApi { CancelResult = new NativeCallResult(false, 87) };
        var service = new Win32RtcWakeService(native);
        await service.SetWakeAsync(WakeTime, CancellationToken.None);

        var clear = await service.ClearAsync(CancellationToken.None);

        Assert.Equal(RtcWakeClearStatus.CleanupFailed, clear.Status);
        Assert.False(clear.Succeeded);
    }

    [Fact]
    public async Task SetWake_ReplacesPreviousTimer()
    {
        var native = new FakeNativeApi();
        var service = new Win32RtcWakeService(native);

        await service.SetWakeAsync(WakeTime, CancellationToken.None);
        await service.SetWakeAsync(WakeTime.AddHours(1), CancellationToken.None);

        // 旧句柄在第二次设置前被取消并关闭；第二次创建 + 设置。
        Assert.Contains(native.Handle, native.CancelledHandles);
        Assert.Contains(native.Handle, native.ClosedHandles);
        Assert.Equal(2, native.CreateCalls);
        Assert.Equal(2, native.SetWakeCalls);
    }

    [Fact]
    public async Task SetWake_WhenCancelled_ThrowsOperationCanceled()
    {
        var service = new Win32RtcWakeService(new FakeNativeApi());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SetWakeAsync(WakeTime, cts.Token));
    }

    private sealed class FakeNativeApi : IRtcWakeNativeApi
    {
        public IntPtr NextHandle { get; set; } = new(0x1234);

        public NativeCallResult SetWakeResult { get; set; } = NativeCallResult.Success();

        public NativeCallResult CancelResult { get; set; } = NativeCallResult.Success();

        public bool CloseResult { get; set; } = true;

        public IntPtr Handle => NextHandle;

        public int CreateCalls { get; private set; }

        public int SetWakeCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public long? SetWakeDueTime { get; private set; }

        public List<IntPtr> CancelledHandles { get; } = [];

        public List<IntPtr> ClosedHandles { get; } = [];

        public IntPtr CreateTimer()
        {
            CreateCalls++;
            return NextHandle;
        }

        public NativeCallResult SetWake(IntPtr timer, long dueTimeFileTimeUtc)
        {
            SetWakeCalls++;
            SetWakeDueTime = dueTimeFileTimeUtc;
            return SetWakeResult;
        }

        public NativeCallResult Cancel(IntPtr timer)
        {
            CancelCalls++;
            CancelledHandles.Add(timer);
            return CancelResult;
        }

        public bool Close(IntPtr timer)
        {
            CloseCalls++;
            ClosedHandles.Add(timer);
            return CloseResult;
        }
    }
}
