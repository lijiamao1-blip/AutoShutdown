using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.Rtc;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C2：RtcWakeAction 受控关机前步骤。未配置/测试模式不触碰服务；能力不支持、
/// 权限不足、固件拒绝、取消一律明确失败（绝不伪造设置成功）；取消直接传播。
/// </summary>
public sealed class S21_RtcWakeActionTests
{
    private static readonly DateTimeOffset WakeTime =
        new(2024, 1, 16, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Execute_WhenNoWakeTime_ReturnsSuccessWithoutServiceAccess()
    {
        var service = new FakeRtcWakeService();
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Shutdown, wakeTime: null),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, service.CapabilityCalls);
        Assert.Equal(0, service.SetWakeCalls);
    }

    [Theory]
    [InlineData(PowerAction.Sleep)]
    [InlineData(PowerAction.Hibernate)]
    public async Task Execute_ForSuspendActions_ArmsWake(PowerAction action)
    {
        var service = new FakeRtcWakeService();
        var actionInstance = CreateAction(service);

        var result = await actionInstance.ExecuteAsync(
            Context(action, WakeTime),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, service.CapabilityCalls);
        Assert.Equal(1, service.SetWakeCalls);
        Assert.Equal(WakeTime, service.LastWakeTime);
    }

    [Fact]
    public async Task Execute_WhenCapabilityNotSupported_ReturnsFailureWithoutSet()
    {
        var service = new FakeRtcWakeService
        {
            Capability = new RtcWakeCapabilityResult
            {
                Status = RtcWakeCapabilityStatus.NotSupported,
                WakeScope = RtcWakeScope.Unknown,
                Reason = "no firmware RTC alarm on this platform"
            }
        };
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Sleep, WakeTime),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not supported", result.ErrorMessage);
        Assert.Equal(0, service.SetWakeCalls);
    }

    [Fact]
    public async Task Execute_ForShutdown_WhenOnlySuspendScope_ReturnsHonestFailure()
    {
        // Windows waitable timer 只能唤醒睡眠/休眠；完全关机（S5）后无法唤醒 → 诚实失败。
        var service = new FakeRtcWakeService
        {
            Capability = new RtcWakeCapabilityResult
            {
                Status = RtcWakeCapabilityStatus.Supported,
                WakeScope = RtcWakeScope.Suspend,
                Reason = "wake from S3/S4 only"
            }
        };
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Shutdown, WakeTime),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot wake this machine from Shutdown", result.ErrorMessage);
        Assert.Equal(0, service.SetWakeCalls);
    }

    [Fact]
    public async Task Execute_ForShutdown_WhenPowerOffScope_ArmsWake()
    {
        var service = new FakeRtcWakeService
        {
            Capability = new RtcWakeCapabilityResult
            {
                Status = RtcWakeCapabilityStatus.Supported,
                WakeScope = RtcWakeScope.SuspendAndPowerOff,
                Reason = "firmware RTC alarm"
            }
        };
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Shutdown, WakeTime),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, service.SetWakeCalls);
    }

    [Fact]
    public async Task Execute_WhenNotAuthorized_ReturnsFailure()
    {
        var service = new FakeRtcWakeService
        {
            SetResult = new RtcWakeSetResult
            {
                Status = RtcWakeSetStatus.NotAuthorized,
                Message = "missing SE_INCREASE_QUOTA_NAME"
            }
        };
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Sleep, WakeTime),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not permitted", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_WhenFirmwareRejected_ReturnsFailure()
    {
        var service = new FakeRtcWakeService
        {
            SetResult = new RtcWakeSetResult
            {
                Status = RtcWakeSetStatus.FirmwareRejected,
                Message = "Win32 error 87"
            }
        };
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Sleep, WakeTime),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_WhenCancelledStatus_ThrowsOperationCanceled()
    {
        var service = new FakeRtcWakeService
        {
            SetResult = new RtcWakeSetResult
            {
                Status = RtcWakeSetStatus.Cancelled,
                Message = "cancelled"
            }
        };
        var action = CreateAction(service);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => action.ExecuteAsync(Context(PowerAction.Sleep, WakeTime), CancellationToken.None));
    }

    [Fact]
    public async Task Execute_WhenServiceThrowsCancellation_Propagates()
    {
        var service = new FakeRtcWakeService { SetThrowsCancelled = true };
        var action = CreateAction(service);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => action.ExecuteAsync(Context(PowerAction.Sleep, WakeTime), CancellationToken.None));
    }

    [Fact]
    public async Task Execute_WhenUnknownSetStatus_FailsClosed()
    {
        var service = new FakeRtcWakeService
        {
            SetResult = new RtcWakeSetResult { Status = RtcWakeSetStatus.Unknown }
        };
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Sleep, WakeTime),
            CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Execute_InTestMode_SkipsArmingAndSucceeds()
    {
        var service = new FakeRtcWakeService();
        var action = new RtcWakeAction(
            service,
            new FixedConfigurationService(ConfigResult(testMode: true)),
            FailurePolicy.Block);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Sleep, WakeTime),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, service.CapabilityCalls);
        Assert.Equal(0, service.SetWakeCalls);
    }

    [Fact]
    public async Task Execute_WhenConfigUnavailable_FailsClosed()
    {
        var service = new FakeRtcWakeService();
        var action = new RtcWakeAction(
            service,
            new FixedConfigurationService(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Corrupt,
                Errors = ["corrupt"]
            }),
            FailurePolicy.Block);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Sleep, WakeTime),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, service.CapabilityCalls);
        Assert.Equal(0, service.SetWakeCalls);
    }

    [Fact]
    public async Task Execute_WhenCapabilityThrows_ReturnsFailure()
    {
        var service = new FakeRtcWakeService { CapabilityThrows = true };
        var action = CreateAction(service);

        var result = await action.ExecuteAsync(
            Context(PowerAction.Sleep, WakeTime),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, service.SetWakeCalls);
    }

    private static RtcWakeAction CreateAction(FakeRtcWakeService service)
        => new(service, new FixedConfigurationService(ConfigResult(testMode: false)));

    private static ConfigurationLoadResult ConfigResult(bool testMode) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = new AppConfig { SchemaVersion = 1, TestMode = testMode }
    };

    private static PrePipelineContext Context(PowerAction action, DateTimeOffset? wakeTime) => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        Action = action,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        RtcWakeTimeUtc = wakeTime
    };

    private sealed class FakeRtcWakeService : IRtcWakeService
    {
        public RtcWakeCapabilityResult Capability { get; set; } = new()
        {
            Status = RtcWakeCapabilityStatus.Supported,
            WakeScope = RtcWakeScope.Suspend,
            Reason = "test platform"
        };

        public RtcWakeSetResult SetResult { get; set; } = new()
        {
            Status = RtcWakeSetStatus.Success,
            Message = "ok"
        };

        public bool CapabilityThrows { get; init; }

        public bool SetThrowsCancelled { get; init; }

        public int CapabilityCalls { get; private set; }

        public int SetWakeCalls { get; private set; }

        public DateTimeOffset? LastWakeTime { get; private set; }

        public Task<RtcWakeCapabilityResult> GetCapabilityAsync(CancellationToken cancellationToken)
        {
            CapabilityCalls++;
            if (CapabilityThrows)
            {
                throw new InvalidOperationException("capability probe failed");
            }

            return Task.FromResult(Capability);
        }

        public Task<RtcWakeSetResult> SetWakeAsync(
            DateTimeOffset wakeTimeUtc,
            CancellationToken cancellationToken)
        {
            SetWakeCalls++;
            LastWakeTime = wakeTimeUtc;
            if (SetThrowsCancelled)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(SetResult);
        }

        public Task<RtcWakeClearResult> ClearAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public FixedConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(
            AppConfig config,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
