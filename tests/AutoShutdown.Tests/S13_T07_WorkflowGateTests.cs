using System.Reflection;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S13-T07 Workflow 双闸门适配验证。
/// 锁定唯一电源出口（仅 ShutdownWorkflow 消费 IPowerService）、V2 实例状态闸门、
/// 以及 config.RealPowerEnabled + instance.RealPowerConfirmed 双闸门在 V2 切面下不变。
/// </summary>
public sealed class S13_T07_WorkflowGateTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ---- 1. 唯一电源出口：Core 内仅 ShutdownWorkflow 消费 IPowerService ----

    [Fact]
    public void OnlyShutdownWorkflowConsumesIPowerServiceInCore()
    {
        var coreAssembly = typeof(ShutdownWorkflow).Assembly;
        var consumers = coreAssembly.GetTypes()
            .Where(type => !type.IsInterface && !type.IsAbstract)
            .Where(type =>
                type.GetConstructors()
                    .SelectMany(ctor => ctor.GetParameters())
                    .Any(parameter => parameter.ParameterType == typeof(IPowerService))
                || type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(field => field.FieldType == typeof(IPowerService)))
            .Select(type => type.Name)
            .ToList();

        Assert.Equal(new[] { nameof(ShutdownWorkflow) }, consumers);
    }

    // ---- 2. V2 实例状态闸门：仅 Executing 可进入电源，其余 V2 态一律 InvalidState 零调用 ----

    [Theory(Timeout = 2000)]
    [InlineData(TaskInstanceState.Unknown)]
    [InlineData(TaskInstanceState.Waiting)]
    [InlineData(TaskInstanceState.Running)]
    [InlineData(TaskInstanceState.Confirming)]
    [InlineData(TaskInstanceState.Executed)]
    [InlineData(TaskInstanceState.Cancelled)]
    [InlineData(TaskInstanceState.Faulted)]
    [InlineData(TaskInstanceState.Interrupted)]
    public async Task Execute_WhenStateIsNotExecuting_RejectsInvalidState_NoPower(TaskInstanceState state)
    {
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { State = state },
            CancellationToken.None);

        Assert.Equal(ShutdownDecisionCode.InvalidState, outcome.DecisionCode);
        Assert.False(outcome.Succeeded);
        Assert.Empty(power.Requests);
    }

    // ---- 3. 双闸门：真实电源模式必须同时满足 RealPowerEnabled + RealPowerConfirmed ----

    [Fact(Timeout = 2000)]
    public async Task RealPowerMode_RequiresEnabledAndConfirmed()
    {
        // 闸门一：RealPowerEnabled=false（未确认也无效）。
        var gateOne = new RecordingPowerService(SimulatedResult());
        var workflowGateOne = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig() with { TestMode = false, RealPowerEnabled = false })),
            gateOne);
        var outcomeOne = await workflowGateOne.ExecuteAsync(
            ValidInstance() with { RealPowerConfirmed = true },
            CancellationToken.None);
        Assert.Equal(ShutdownDecisionCode.RealPowerNotEnabled, outcomeOne.DecisionCode);
        Assert.Empty(gateOne.Requests);

        // 闸门二：RealPowerEnabled=true 但实例未确认。
        var gateTwo = new RecordingPowerService(SimulatedResult());
        var workflowGateTwo = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig() with { TestMode = false, RealPowerEnabled = true })),
            gateTwo);
        var outcomeTwo = await workflowGateTwo.ExecuteAsync(
            ValidInstance() with { RealPowerConfirmed = false },
            CancellationToken.None);
        Assert.Equal(ShutdownDecisionCode.RealPowerConfirmationMissing, outcomeTwo.DecisionCode);
        Assert.Empty(gateTwo.Requests);
    }

    // ---- Helpers ----

    private static ConfigurationLoadResult SuccessResult(AppConfig config) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = config
    };

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
