using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S16 C3 集成：ShutdownWorkflow 与 PrePipelineRunner 的接线契约。
/// 确认成功（双闸门 + 状态/身份/动作/时序校验通过）后运行 Runner；
/// 仅当 Runner 返回 PowerAllowed 时才进入唯一电源出口；block 立即取消电源意图。
/// 全部用例使用替身电源，绝不触发真实关机/重启/睡眠/休眠。
/// </summary>
public sealed class S16_WorkflowPrePipelineIntegrationTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenPipelineAllows_RunsActionsThenCallsPowerOnce()
    {
        var action = new RecordingAction("OfficeSave", FailurePolicy.Continue);
        var runner = new PrePipelineRunner([action]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(ShutdownWorkflowStatus.Simulated, result.Status);
        Assert.NotNull(result.PrePipeline);
        Assert.True(result.PrePipeline.PowerAllowed);
        Assert.Equal(PrePipelineRunStatus.Completed, result.PrePipeline.Status);
        Assert.Single(result.PrePipeline.Actions);

        // 上下文只携带审计所需信息，绝不暴露 IPowerService。
        var context = action.LastContext;
        Assert.NotNull(context);
        Assert.Equal(InstanceId1, context.InstanceId);
        Assert.Equal(SourceTaskId, context.SourceTaskId);
        Assert.Equal(PowerAction.Shutdown, context.Action);

        Assert.Single(power.Requests);
        Assert.Equal(InstanceId1, power.Requests[0].InstanceId);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenBlockActionFails_ReturnsPrePipelineBlockedWithoutPower()
    {
        var runner = new PrePipelineRunner([
            new FailingAction("CloseApps", FailurePolicy.Block, "locked app")
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownWorkflowStatus.Rejected, result.Status);
        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        Assert.NotNull(result.PrePipeline);
        Assert.False(result.PrePipeline.PowerAllowed);
        Assert.Equal(PrePipelineRunStatus.Blocked, result.PrePipeline.Status);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenContinueActionFails_AllowsPowerAndKeepsAudit()
    {
        var runner = new PrePipelineRunner([
            new FailingAction("RunCommands", FailurePolicy.Continue, "command failed")
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.PrePipeline);
        Assert.True(result.PrePipeline.PowerAllowed);
        Assert.Single(result.PrePipeline.Actions);
        Assert.False(result.PrePipeline.Actions[0].Succeeded);
        Assert.Equal(FailurePolicy.Continue, result.PrePipeline.Actions[0].FailurePolicy);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenActionThrowsWithContinue_RecordsDesensitizedFailureAndAllowsPower()
    {
        var runner = new PrePipelineRunner([
            new ThrowingAction("RunCommands", FailurePolicy.Continue, "secret  token")
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.PrePipeline);
        Assert.Single(result.PrePipeline.Actions);
        Assert.False(result.PrePipeline.Actions[0].Succeeded);
        // 脱敏：控制字符被折叠为空格。
        Assert.DoesNotContain('', result.PrePipeline.Actions[0].ErrorMessage);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenPowerServiceFails_PreservesPrePipelineAudit()
    {
        var runner = new PrePipelineRunner([
            new RecordingAction("OfficeSave", FailurePolicy.Continue)
        ]);
        var power = new RecordingPowerService(new PowerResult
        {
            Outcome = PowerOutcome.Failed,
            WasSimulated = true,
            Message = "power failed"
        });
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDecisionCode.PowerServiceFailed, result.DecisionCode);
        Assert.NotNull(result.PrePipeline);
        Assert.True(result.PrePipeline.PowerAllowed);
        Assert.Single(result.PrePipeline.Actions);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WithDefaultEmptyRunner_AttachesCompletedPipelineAndCallsPower()
    {
        // 二参构造回退到 PrePipelineRunner.Empty：空流水线即 Completed → 允许电源。
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.PrePipeline);
        Assert.Equal(PrePipelineRunStatus.Completed, result.PrePipeline.Status);
        Assert.Empty(result.PrePipeline.Actions);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task Execute_WhenCancelledDuringPipeline_PropagatesWithoutPower()
    {
        var runner = new PrePipelineRunner([
            new RecordingAction("OfficeSave", FailurePolicy.Continue)
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => workflow.ExecuteAsync(ValidInstance(), cts.Token));

        Assert.Empty(power.Requests);
    }

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

    private sealed class RecordingAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;

        public RecordingAction(string name, FailurePolicy policy)
        {
            _name = name;
            _policy = policy;
        }

        public PrePipelineContext? LastContext { get; private set; }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.FromResult(new PrePipelineActionResult { Succeeded = true });
        }
    }

    private sealed class FailingAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;
        private readonly string _error;

        public FailingAction(string name, FailurePolicy policy, string error)
        {
            _name = name;
            _policy = policy;
            _error = error;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(new PrePipelineActionResult { Succeeded = false, ErrorMessage = _error });
    }

    private sealed class ThrowingAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;
        private readonly string _message;

        public ThrowingAction(string name, FailurePolicy policy, string message)
        {
            _name = name;
            _policy = policy;
            _message = message;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(_message);
    }

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

        public Task<PowerResult> ExecuteAsync(
            PowerRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_result);
        }
    }
}
