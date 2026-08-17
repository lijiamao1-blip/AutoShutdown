using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.Rtc;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C2：ShutdownWorkflow 的 RTC 唤醒清除契约。Pre-Pipeline 若成功武装了一次性 RTC
/// 唤醒，则电源未被接受（拒绝/失败/取消）时必须清除；清除失败明确上报。电源被接受时
/// 保留唤醒（随本次关机/睡眠）。全部用例使用替身，绝不触发真实电源/定时器。
/// </summary>
public sealed class S21_ShutdownWorkflowRtcCleanupTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Execute_WhenPowerRejectedAndRtcArmed_ClearsWake()
    {
        var rtc = new RecordingRtcWakeService();
        var workflow = CreateWorkflow(
            PowerResultRejected(),
            rtc: rtc,
            armed: true);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownWorkflowStatus.Rejected, result.Status);
        Assert.Equal(1, rtc.ClearCalls);
    }

    [Fact]
    public async Task Execute_WhenPowerAcceptedAndRtcArmed_KeepsWake()
    {
        var rtc = new RecordingRtcWakeService();
        var workflow = CreateWorkflow(
            new PowerResult { Outcome = PowerOutcome.Accepted, Message = "accepted" },
            rtc: rtc,
            armed: true);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownWorkflowStatus.Accepted, result.Status);
        Assert.Equal(0, rtc.ClearCalls);
    }

    [Fact]
    public async Task Execute_WhenPowerFailedAndClearFails_ReportsCleanupFailure()
    {
        var rtc = new RecordingRtcWakeService
        {
            ClearResult = new RtcWakeClearResult
            {
                Status = RtcWakeClearStatus.CleanupFailed,
                Message = "CancelWaitableTimer failed"
            }
        };
        var workflow = CreateWorkflow(
            new PowerResult { Outcome = PowerOutcome.Failed, Message = "power failed" },
            rtc: rtc,
            armed: true);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownWorkflowStatus.PowerFailed, result.Status);
        Assert.Equal(1, rtc.ClearCalls);
        Assert.Contains("RTC wake cleanup failed", result.Message);
    }

    [Fact]
    public async Task Execute_WhenPowerRejectedAndNotArmed_DoesNotClear()
    {
        var rtc = new RecordingRtcWakeService();
        var workflow = CreateWorkflow(
            PowerResultRejected(),
            rtc: rtc,
            armed: false);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownWorkflowStatus.Rejected, result.Status);
        Assert.Equal(0, rtc.ClearCalls);
    }

    [Fact]
    public async Task Execute_WhenPowerCancelledAndRtcArmed_ClearsThenRethrows()
    {
        var rtc = new RecordingRtcWakeService();
        var workflow = CreateWorkflow(
            power: new ThrowingPowerService(new OperationCanceledException()),
            rtc: rtc,
            armed: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => workflow.ExecuteAsync(ValidInstance(), CancellationToken.None));

        Assert.Equal(1, rtc.ClearCalls);
    }

    [Fact]
    public async Task Execute_WhenPowerServiceThrowsAndRtcArmed_ClearsAndReportsCleanupFailure()
    {
        var rtc = new RecordingRtcWakeService
        {
            ClearResult = new RtcWakeClearResult
            {
                Status = RtcWakeClearStatus.CleanupFailed,
                Message = "cancel failed"
            }
        };
        var workflow = CreateWorkflow(
            power: new ThrowingPowerService(new InvalidOperationException("boom")),
            rtc: rtc,
            armed: true);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownWorkflowStatus.PowerFailed, result.Status);
        Assert.Equal(ShutdownDecisionCode.PowerServiceException, result.DecisionCode);
        Assert.Equal(1, rtc.ClearCalls);
        Assert.Contains("RTC wake cleanup failed", result.Message);
    }

    private static ShutdownWorkflow CreateWorkflow(
        PowerResult? powerResult = null,
        IPowerService? power = null,
        RecordingRtcWakeService? rtc = null,
        bool armed = false)
    {
        var pipeline = new FixedPipelineRunner(new PrePipelineRunResult
        {
            Status = PrePipelineRunStatus.Completed,
            Actions = armed
                ? [new PrePipelineActionResult { ActionName = RtcWakeAction.ActionName, Succeeded = true }]
                : []
        });

        return new ShutdownWorkflow(
            new FixedConfigurationService(ValidConfig()),
            power ?? new FixedPowerService(powerResult ?? PowerResultRejected()),
            pipeline,
            rtcWakeService: rtc);
    }

    private static PowerResult PowerResultRejected() => new()
    {
        Outcome = PowerOutcome.Rejected,
        Message = "rejected"
    };

    private static ConfigurationLoadResult ValidConfig() => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = new AppConfig
        {
            SchemaVersion = 1,
            TestMode = false,
            RealPowerEnabled = true,
            AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate]
        }
    };

    private static TaskInstance ValidInstance() => new()
    {
        InstanceId = InstanceId,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = StageToken,
        HasExecuted = true,
        RealPowerConfirmed = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private sealed class FixedPipelineRunner : IPrePipelineRunner
    {
        private readonly PrePipelineRunResult _result;

        public FixedPipelineRunner(PrePipelineRunResult result) => _result = result;

        public Task<PrePipelineRunResult> RunAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(_result);
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

    private sealed class FixedPowerService : IPowerService
    {
        private readonly PowerResult _result;

        public FixedPowerService(PowerResult result) => _result = result;

        public Task<PowerResult> ExecuteAsync(
            PowerRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }

    private sealed class ThrowingPowerService : IPowerService
    {
        private readonly Exception _exception;

        public ThrowingPowerService(Exception exception) => _exception = exception;

        public Task<PowerResult> ExecuteAsync(
            PowerRequest request,
            CancellationToken cancellationToken)
            => throw _exception;
    }

    private sealed class RecordingRtcWakeService : IRtcWakeService
    {
        public RtcWakeClearResult ClearResult { get; set; } = new()
        {
            Status = RtcWakeClearStatus.Success,
            Message = "cleared"
        };

        public int ClearCalls { get; private set; }

        public Task<RtcWakeCapabilityResult> GetCapabilityAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RtcWakeSetResult> SetWakeAsync(
            DateTimeOffset wakeTimeUtc,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RtcWakeClearResult> ClearAsync(CancellationToken cancellationToken)
        {
            ClearCalls++;
            return Task.FromResult(ClearResult);
        }
    }
}
