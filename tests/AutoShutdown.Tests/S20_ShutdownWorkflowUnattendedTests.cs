using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Unattended;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S20 检查点 3：无人值守等效确认在 ShutdownWorkflow 双闸门之二处的接入。
/// 无人值守仅替代人工确认事实；冻结状态机/双闸门/Pre-Pipeline 顺序/唯一电源调用均不变。
/// </summary>
public sealed class S20_ShutdownWorkflowUnattendedTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly UnattendedConfirmationEvaluator _evaluator = new();

    [Fact]
    public async Task Execute_WhenRealPowerAndUnattendedAuthorized_GrantsEquivalentConfirmationAndCallsPowerOnce()
    {
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: new FixedUnattendedPolicyService(action => Authorized(action)),
            unattendedEvaluator: _evaluator);

        var outcome = await workflow.ExecuteAsync(ValidInstance(useUnattended: true), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(ShutdownWorkflowStatus.Accepted, outcome.Status);
        Assert.Equal(ShutdownDecisionCode.Allowed, outcome.DecisionCode);

        // 等效确认满足双闸门之二：请求携带 RealPowerConfirmed=true（GuardedPowerService 第二闸门放行）。
        Assert.Single(power.Requests);
        Assert.True(power.Requests[0].RealPowerConfirmed);

        // 审计字段：记录授权版本/时刻/触发原因。
        Assert.NotNull(outcome.UnattendedConfirmation);
        Assert.True(outcome.UnattendedConfirmation!.AllowsPower);
        Assert.Equal(3, outcome.UnattendedConfirmation.Authorization!.Policy!.AuthorizationVersion);
    }

    [Fact]
    public async Task Execute_WhenRealPowerAndUnattendedDenied_ReturnsUnattendedNotAuthorizedWithoutPower()
    {
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: new FixedUnattendedPolicyService(_ =>
                UnattendedAuthorizationDecision.Denied(UnattendedPolicyStatus.Expired, "expired")),
            unattendedEvaluator: _evaluator);

        var outcome = await workflow.ExecuteAsync(ValidInstance(useUnattended: true), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.UnattendedNotAuthorized, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task Execute_WhenRealPowerAndActionMismatch_ReturnsUnattendedNotAuthorized()
    {
        var power = new RecordingPowerService(AcceptedResult());
        // 授权仅覆盖 Shutdown；实例动作为 Hibernate → 策略层返回 ActionMismatch（fail-closed）。
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: new FixedUnattendedPolicyService(action =>
                action == PowerAction.Shutdown
                    ? Authorized(PowerAction.Shutdown)
                    : UnattendedAuthorizationDecision.Denied(UnattendedPolicyStatus.ActionMismatch, "mismatch")),
            unattendedEvaluator: _evaluator);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance(useUnattended: true) with { ActionSnapshot = PowerAction.Hibernate },
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.UnattendedNotAuthorized, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task Execute_WhenRealPowerAndPolicyServiceThrows_FailClosedUnattendedNotAuthorized()
    {
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: new FixedUnattendedPolicyService(
                _ => throw new InvalidOperationException("storage failure")),
            unattendedEvaluator: _evaluator);

        var outcome = await workflow.ExecuteAsync(ValidInstance(useUnattended: true), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.UnattendedNotAuthorized, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task Execute_WhenRealPowerAndNoUnattendedService_ReturnsRealPowerConfirmationMissing()
    {
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power);

        var outcome = await workflow.ExecuteAsync(ValidInstance(useUnattended: true), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ShutdownDecisionCode.RealPowerConfirmationMissing, outcome.DecisionCode);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public async Task Execute_WhenManualConfirmed_ProceedsWithoutUnattendedAudit()
    {
        var power = new RecordingPowerService(AcceptedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: new FixedUnattendedPolicyService(action => Authorized(action)),
            unattendedEvaluator: _evaluator);

        var outcome = await workflow.ExecuteAsync(
            ValidInstance() with { RealPowerConfirmed = true },
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(power.Requests[0].RealPowerConfirmed);
        // 人工确认路径不产生无人值守审计字段。
        Assert.Null(outcome.UnattendedConfirmation);
    }

    [Fact]
    public async Task Execute_WhenTestMode_DoesNotEvaluateUnattendedAndKeepsSimulatedPath()
    {
        // TestMode=true：无需无人值守评估，仍走模拟路径；审计字段为 null。
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(TestModeConfig()),
            power,
            unattendedPolicyService: new FixedUnattendedPolicyService(
                _ => throw new InvalidOperationException("must not be called")),
            unattendedEvaluator: _evaluator);

        var outcome = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(ShutdownWorkflowStatus.Simulated, outcome.Status);
        Assert.Null(outcome.UnattendedConfirmation);
        Assert.Single(power.Requests);
    }

    private static AppConfig RealPowerConfig() => ValidConfig() with
    {
        TestMode = false,
        RealPowerEnabled = true
    };

    private static AppConfig TestModeConfig() => ValidConfig() with { TestMode = true };

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskInstance ValidInstance(bool useUnattended = false) => new()
    {
        InstanceId = InstanceId1,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = StageToken1,
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
        UseUnattended = useUnattended
    };

    private static UnattendedAuthorizationDecision Authorized(PowerAction action)
        => UnattendedAuthorizationDecision.Granted(new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 3,
            AuthorizedAtUtc = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            AuthorizedAction = action,
            TriggerReason = "nightly"
        });

    private static PowerResult AcceptedResult() => new()
    {
        Outcome = PowerOutcome.Accepted,
        WasSimulated = false,
        Message = "real power accepted"
    };

    private static PowerResult SimulatedResult() => new()
    {
        Outcome = PowerOutcome.Simulated,
        WasSimulated = true,
        Message = "simulated"
    };

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public FixedConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedUnattendedPolicyService : IUnattendedPolicyService
    {
        private readonly Func<PowerAction, UnattendedAuthorizationDecision> _evaluate;

        public FixedUnattendedPolicyService(Func<PowerAction, UnattendedAuthorizationDecision> evaluate)
            => _evaluate = evaluate;

        public Task<UnattendedAuthorizationDecision> EvaluateAsync(
            PowerAction action,
            CancellationToken cancellationToken) => Task.FromResult(_evaluate(action));

        public Task<UnattendedEnableResult> EnableAsync(
            UnattendedEnableRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UnattendedRevokeResult> RevokeAsync(CancellationToken cancellationToken)
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
