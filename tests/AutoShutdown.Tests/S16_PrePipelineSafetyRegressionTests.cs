using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S16 独立验收缺陷回归（PrePipelineRunner 安全缺口）：
///  缺陷一：主动返回的错误文本未脱敏 —— 所有失败路径必须收敛到同一 SanitizeError；
///  缺陷二：FailurePolicy.Unknown 意外放行电源 —— 仅明确 Continue 允许失败后继续，
///          Block/Unknown/未定义枚举一律 fail-closed。
/// 全部用例使用替身电源，绝不触发真实关机/重启/睡眠/休眠。
/// </summary>
public sealed class S16_PrePipelineSafetyRegressionTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ===================== 缺陷一：主动返回的错误文本未脱敏 =====================

    [Fact(Timeout = 2000)]
    public async Task ExplicitFailure_ErrorMessage_IsSanitized()
    {
        // Action 主动返回失败结果：包含 \r\n、控制字符和超过 200 字符的消息。
        var raw = "save failed\r\n[FAKE]" + new string('A', 300);
        var runner = new PrePipelineRunner([
            new FixedResultAction("OfficeSave", FailurePolicy.Continue, succeeded: false, error: raw)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        var recorded = Assert.Single(result.Actions);
        Assert.False(recorded.Succeeded);
        // 长度受限：不超过 200。
        Assert.True(recorded.ErrorMessage.Length <= 200,
            $"ErrorMessage length {recorded.ErrorMessage.Length} exceeds 200.");
        // 控制字符被折叠。
        Assert.All(recorded.ErrorMessage, character => Assert.False(char.IsControl(character)));
        // 非控制字符内容仍保留（截断后）。
        Assert.Contains("save failed", recorded.ErrorMessage);
    }

    [Fact(Timeout = 2000)]
    public async Task NullReturn_Failure_IsSanitizedAtConvergencePoint()
    {
        var runner = new PrePipelineRunner([
            new NullReturnAction(FailurePolicy.Continue)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        var recorded = Assert.Single(result.Actions);
        Assert.False(recorded.Succeeded);
        Assert.False(string.IsNullOrEmpty(recorded.ErrorMessage));
        Assert.All(recorded.ErrorMessage, character => Assert.False(char.IsControl(character)));
    }

    [Fact(Timeout = 2000)]
    public async Task SuccessResult_ErrorMessage_IsClearedEvenIfActionSuppliedText()
    {
        // 成功结果不得携带未经处理的错误文本：即使 Action 恶意/错误地在成功里塞错误文本。
        var runner = new PrePipelineRunner([
            new FixedResultAction("OfficeSave", FailurePolicy.Continue,
                succeeded: true, error: "should not leak\r\n" + new string('B', 300))
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        var recorded = Assert.Single(result.Actions);
        Assert.True(recorded.Succeeded);
        Assert.Equal(string.Empty, recorded.ErrorMessage);
    }

    // ===================== 缺陷二：FailurePolicy.Unknown 意外放行电源 =====================

    [Theory(Timeout = 2000)]
    [InlineData(FailurePolicy.Unknown)]
    [InlineData((FailurePolicy)999)]
    public async Task UnknownOrUndefinedPolicyFailure_FailsClosed_NotPowerAllowed(FailurePolicy policy)
    {
        var runner = new PrePipelineRunner([
            new FixedResultAction("RunCommands", policy, succeeded: false, error: "failed")
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Blocked, result.Status);
        Assert.False(result.PowerAllowed);
    }

    [Theory(Timeout = 2000)]
    [InlineData(FailurePolicy.Unknown)]
    [InlineData((FailurePolicy)999)]
    public async Task UnknownOrUndefinedPolicyFailure_StopsSubsequentActions(FailurePolicy policy)
    {
        var order = new List<string>();
        var runner = new PrePipelineRunner([
            new RecordingAction("first", FailurePolicy.Continue, order, succeed: true),
            new RecordingAction("unknown", policy, order, succeed: false, error: "failed"),
            new RecordingAction("never", FailurePolicy.Continue, order, succeed: true)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Blocked, result.Status);
        Assert.Equal(["first", "unknown"], order); // 后续动作不得执行
    }

    [Theory(Timeout = 2000)]
    [InlineData(FailurePolicy.Unknown)]
    [InlineData((FailurePolicy)999)]
    public async Task Workflow_UnknownOrUndefinedPolicyFailure_DoesNotCallPower(FailurePolicy policy)
    {
        var runner = new PrePipelineRunner([
            new FixedResultAction("RunCommands", policy, succeeded: false, error: "failed")
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
        Assert.Empty(power.Requests); // IPowerService 调用次数必须为 0
    }

    // ===================== 既有语义保持 =====================

    [Fact(Timeout = 2000)]
    public async Task ContinueFailure_StillAllowsPower()
    {
        var runner = new PrePipelineRunner([
            new FixedResultAction("RunCommands", FailurePolicy.Continue, succeeded: false, error: "warn")
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task BlockFailure_StillBlocksPower()
    {
        var runner = new PrePipelineRunner([
            new FixedResultAction("CloseApps", FailurePolicy.Block, succeeded: false, error: "locked")
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        Assert.Empty(power.Requests);
    }

    private static PrePipelineContext Context() => new()
    {
        InstanceId = InstanceId1,
        SourceTaskId = SourceTaskId,
        Action = PowerAction.Shutdown,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero)
    };

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

    /// <summary>返回固定成功/失败结果的动作（错误文本按原样返回，暴露未脱敏缺陷）。</summary>
    private sealed class FixedResultAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;
        private readonly bool _succeeded;
        private readonly string _error;

        public FixedResultAction(string name, FailurePolicy policy, bool succeeded, string error)
        {
            _name = name;
            _policy = policy;
            _succeeded = succeeded;
            _error = error;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(new PrePipelineActionResult { Succeeded = _succeeded, ErrorMessage = _error });
    }

    private sealed class NullReturnAction : IPreShutdownAction
    {
        private readonly FailurePolicy _policy;

        public NullReturnAction(FailurePolicy policy) => _policy = policy;

        public string Name => "null-return";

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
            => Task.FromResult<PrePipelineActionResult>(null!);
    }

    private sealed class RecordingAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;
        private readonly List<string> _order;
        private readonly bool _succeed;
        private readonly string _error;

        public RecordingAction(string name, FailurePolicy policy, List<string> order, bool succeed, string error = "")
        {
            _name = name;
            _policy = policy;
            _order = order;
            _succeed = succeed;
            _error = error;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            _order.Add(_name);
            return Task.FromResult(new PrePipelineActionResult { Succeeded = _succeed, ErrorMessage = _error });
        }
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
