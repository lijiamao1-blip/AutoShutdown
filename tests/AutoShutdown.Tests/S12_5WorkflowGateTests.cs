using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.5 工作流双闸门测试。验证 ShutdownWorkflow 在真实电源模式下的
/// RealPowerNotEnabled / RealPowerConfirmationMissing / 通过三态，全部使用替身电源。
/// </summary>
public sealed class S12_5WorkflowGateTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ---- 1. TestMode=false 且 RealPowerEnabled=false → RealPowerNotEnabled，零调用 ----

    [Fact]
    public async Task TestModeFalse_RealPowerNotEnabled_ReturnsRealPowerNotEnabled_NoPowerCall()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(Config(testMode: false, realPowerEnabled: false))),
            power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.RealPowerNotEnabled, outcome.DecisionCode);
        Assert.False(outcome.Succeeded);
        Assert.Empty(power.Requests);
    }

    // ---- 2. TestMode=false、RealPowerEnabled=true、实例未确认 → RealPowerConfirmationMissing ----

    [Fact]
    public async Task RealPowerEnabled_InstanceNotConfirmed_ReturnsConfirmationMissing_NoPowerCall()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(Config(testMode: false, realPowerEnabled: true))),
            power);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { RealPowerConfirmed = false },
            CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.RealPowerConfirmationMissing, outcome.DecisionCode);
        Assert.False(outcome.Succeeded);
        Assert.Empty(power.Requests);
    }

    // ---- 3. TestMode=false、RealPowerEnabled=true、实例已确认 → 通过并调用电源 ----

    [Fact]
    public async Task RealPowerEnabled_InstanceConfirmed_PassesThrough_ToPower()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(Config(testMode: false, realPowerEnabled: true))),
            power);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { RealPowerConfirmed = true },
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Single(power.Requests);
        Assert.True(power.Requests[0].RealPowerConfirmed);
    }

    // ---- 4. TestMode=true 的既有模拟路径行为不变 ----

    [Fact]
    public async Task TestModeTrue_SimulatedPath_Unchanged()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(Config(testMode: true, realPowerEnabled: false))),
            power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownWorkflowStatus.Simulated, outcome.Status);
        Assert.Equal(ShutdownDecisionCode.Allowed, outcome.DecisionCode);
        Assert.True(outcome.Succeeded);
        Assert.Single(power.Requests);
    }

    // ---- Helpers ----

    private static AppConfig Config(bool testMode, bool realPowerEnabled) => new()
    {
        SchemaVersion = 1,
        TestMode = testMode,
        RealPowerEnabled = realPowerEnabled,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static ConfigurationLoadResult SuccessResult(AppConfig config) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = config
    };

    private static TaskInstance ValidInstance() => new()
    {
        InstanceId = InstanceId1,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = StageToken1,
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
        RealPowerConfirmed = true
    };

    private static PowerResult SimulatedResult() => new()
    {
        Outcome = PowerOutcome.Simulated,
        WasSimulated = true,
        Message = "simulated"
    };

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public FixedConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPowerService : IPowerService
    {
        private readonly List<PowerRequest> _requests = new();
        private readonly PowerResult _result;

        public RecordingPowerService(PowerResult result) => _result = result;

        public IReadOnlyList<PowerRequest> Requests => _requests;

        public Task<PowerResult> ExecuteAsync(PowerRequest request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_result);
        }
    }
}
