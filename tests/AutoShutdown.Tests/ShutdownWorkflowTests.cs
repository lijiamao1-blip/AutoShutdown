using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class ShutdownWorkflowTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenConfigurationUnavailable_ReturnsConfigurationUnavailableWithoutPower()
    {
        foreach (var result in CreateUnavailableResults())
        {
            var power = new RecordingPowerService(SimulatedResult());
            var workflow = new ShutdownWorkflow(new FixedConfigurationService(result), power);

            var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

            Assert.Equal(ShutdownDecisionCode.ConfigurationUnavailable, outcome.DecisionCode);
            Assert.False(outcome.Succeeded);
            Assert.Empty(power.Requests);
        }
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenConfigurationLoadThrows_ReturnsConfigurationUnavailableWithoutPower()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new ThrowingConfigurationService(), power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.ConfigurationUnavailable, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenTestModeDisabledAndRealPowerNotEnabled_ReturnsRealPowerNotEnabledWithoutPower()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig() with { TestMode = false })),
            power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.RealPowerNotEnabled, outcome.DecisionCode);
        Assert.False(outcome.Succeeded);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenStateIsNotExecuting_ReturnsInvalidStateWithoutPower()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { State = TaskInstanceState.Waiting },
            CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.InvalidState, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenExecutionFlagMissing_ReturnsExecutionFlagMissingWithoutPower()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { HasExecuted = false },
            CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.ExecutionFlagMissing, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Theory(Timeout = 2000)]
    [InlineData("InstanceId")]
    [InlineData("SourceTaskId")]
    [InlineData("StageToken")]
    public async Task Execute_WhenIdentityFieldIsEmpty_ReturnsInvalidIdentityWithoutPower(string field)
    {
        var instance = field switch
        {
            "InstanceId" => ValidInstance() with { InstanceId = Guid.Empty },
            "SourceTaskId" => ValidInstance() with { SourceTaskId = Guid.Empty },
            _ => ValidInstance() with { StageToken = Guid.Empty }
        };
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(instance, CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.InvalidIdentity, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Theory(Timeout = 2000)]
    [InlineData(PowerAction.Unknown)]
    [InlineData((PowerAction)99)]
    public async Task Execute_WhenActionIsInvalid_ReturnsInvalidActionWithoutPower(PowerAction action)
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { ActionSnapshot = action },
            CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.InvalidAction, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenActionNotAllowedByConfiguration_ReturnsActionNotAllowedWithoutFallback()
    {
        var config = ValidConfig() with { AllowedActions = [PowerAction.Sleep] };
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(config)), power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.ActionNotAllowed, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Theory(Timeout = 2000)]
    [InlineData("CreatedAtDefault")]
    [InlineData("ScheduledFireTimeDefault")]
    [InlineData("FireBeforeCreatedAt")]
    public async Task Execute_WhenTimingIsInvalid_ReturnsInvalidTimingWithoutPower(string scenario)
    {
        var instance = scenario switch
        {
            "CreatedAtDefault" => ValidInstance() with { CreatedAt = default },
            "ScheduledFireTimeDefault" => ValidInstance() with { ScheduledFireTime = default },
            _ => ValidInstance() with { ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero) }
        };
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(instance, CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.InvalidTiming, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Theory(Timeout = 2000)]
    [InlineData(PowerAction.Shutdown)]
    [InlineData(PowerAction.Restart)]
    [InlineData(PowerAction.Sleep)]
    [InlineData(PowerAction.Hibernate)]
    public async Task Execute_ForEachAllowedAction_CallsPowerOnceWithMatchingRequest(PowerAction action)
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { ActionSnapshot = action },
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(ShutdownWorkflowStatus.Simulated, outcome.Status);
        Assert.Equal(ShutdownDecisionCode.Allowed, outcome.DecisionCode);
        Assert.NotNull(outcome.PowerResult);
        Assert.Single(power.Requests);
        Assert.Equal(action, power.Requests[0].Action);
        Assert.Equal(InstanceId1, power.Requests[0].InstanceId);
        Assert.False(string.IsNullOrEmpty(power.Requests[0].Reason));
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenPowerReturnsSimulated_Succeeds()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.Allowed, outcome.DecisionCode);
    }

    [Theory(Timeout = 2000)]
    [InlineData(PowerOutcome.Rejected, true, ShutdownWorkflowStatus.Rejected, ShutdownDecisionCode.PowerServiceRejected)]
    [InlineData(PowerOutcome.Failed, true, ShutdownWorkflowStatus.PowerFailed, ShutdownDecisionCode.PowerServiceFailed)]
    [InlineData(PowerOutcome.Unknown, true, ShutdownWorkflowStatus.PowerFailed, ShutdownDecisionCode.PowerServiceFailed)]
    [InlineData(PowerOutcome.Simulated, false, ShutdownWorkflowStatus.PowerFailed, ShutdownDecisionCode.PowerServiceFailed)]
    public async Task Execute_WhenPowerResultIsNotExpectedSimulated_SafelyFails(
        PowerOutcome outcome,
        bool wasSimulated,
        ShutdownWorkflowStatus expectedStatus,
        ShutdownDecisionCode expectedDecision)
    {
        var power = new RecordingPowerService(new PowerResult
        {
            Outcome = outcome,
            WasSimulated = wasSimulated,
            Message = "unexpected"
        });
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedDecision, result.DecisionCode);
        Assert.False(result.Succeeded);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenPowerReturnsAccepted_SucceedsWithAcceptedStatus()
    {
        var power = new RecordingPowerService(new PowerResult
        {
            Outcome = PowerOutcome.Accepted,
            WasSimulated = false,
            Message = "real power accepted"
        });
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig() with { TestMode = false, RealPowerEnabled = true })),
            power);

        var result = await workflow.ExecuteAsync(
            ValidInstance() with { RealPowerConfirmed = true },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(ShutdownWorkflowStatus.Accepted, result.Status);
        Assert.Equal(ShutdownDecisionCode.Allowed, result.DecisionCode);
        Assert.NotNull(result.PowerResult);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenPowerServiceThrows_ReturnsPowerServiceExceptionWithoutRetry()
    {
        var power = new RecordingPowerService(() => throw new InvalidOperationException("Simulated power failure."));
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.Equal(ShutdownWorkflowStatus.PowerFailed, outcome.Status);
        Assert.Equal(ShutdownDecisionCode.PowerServiceException, outcome.DecisionCode);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenCancelled_PropagatesOperationCanceledExceptionWithoutRetry()
    {
        var power = new RecordingPowerService(() => throw new OperationCanceledException());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => workflow.ExecuteAsync(ValidInstance(), cts.Token));

        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_TwoInvocationsAreIndependentRequests()
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(new FixedConfigurationService(SuccessResult(ValidConfig())), power);

        var first = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);
        var second = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, power.Requests.Count);
    }

    private static ConfigurationLoadResult SuccessResult(AppConfig config) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = config
    };

    private static List<ConfigurationLoadResult> CreateUnavailableResults() =>
    [
            new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Missing, Errors = ["missing"] },
            new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Corrupt, Errors = ["corrupt"] },
            new ConfigurationLoadResult { Status = ConfigurationLoadStatus.IoFailure, Errors = ["io"] },
            new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Invalid, Errors = ["invalid"] },
            new ConfigurationLoadResult { Status = ConfigurationLoadStatus.UnsupportedVersion, Errors = ["version"] },
        new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Success, Config = null }
    ];

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskInstance ValidInstance() => new()
    {
        InstanceId = InstanceId1,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = StageToken1,
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
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

    private sealed class ThrowingConfigurationService : IConfigurationService
    {
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated load failure.");

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPowerService : IPowerService
    {
        private readonly List<PowerRequest> _requests = new();
        private readonly PowerResult? _result;
        private readonly Func<Task<PowerResult>>? _custom;

        public RecordingPowerService(PowerResult result) => _result = result;

        public RecordingPowerService(Func<Task<PowerResult>> custom) => _custom = custom;

        public IReadOnlyList<PowerRequest> Requests => _requests;

        public Task<PowerResult> ExecuteAsync(
            PowerRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            if (_custom is not null)
            {
                return _custom();
            }

            return Task.FromResult(_result!);
        }
    }
}
