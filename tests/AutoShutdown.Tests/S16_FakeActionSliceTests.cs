using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure.CloseApps;
using AutoShutdown.App.Infrastructure.Commands;
using AutoShutdown.App.Infrastructure.Office;
using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S16 C4 切片与候选：用 Fake Action 端到端验证固定顺序、取消、异常与策略，
/// 并对组合根做安全冒烟（生产默认 = Empty 流水线 + GuardedPowerService，无真实电源）。
/// 全部用例使用替身电源，绝不触发真实关机/重启/睡眠/休眠。
/// </summary>
public sealed class S16_FakeActionSliceTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact(Timeout = 2000)]
    public async Task FakeActions_RunInFrozenOrder_EndToEnd()
    {
        var log = new List<string>();
        var runner = new PrePipelineRunner([
            SliceAction.Succeed("OfficeSave", log),
            SliceAction.Succeed("RunCommands", log),
            SliceAction.Succeed("CloseApps", log)
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        // 框架拥有顺序：OfficeSave → RunCommands → CloseApps（S17+ 固定）。
        Assert.Equal(["OfficeSave", "RunCommands", "CloseApps"], log);
        Assert.NotNull(result.PrePipeline);
        Assert.Equal(3, result.PrePipeline.Actions.Count);
        Assert.All(result.PrePipeline.Actions, action => Assert.True(action.Succeeded));
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task FakeActions_BlockFailure_StopsSubsequentActionsAndPower()
    {
        var log = new List<string>();
        var runner = new PrePipelineRunner([
            SliceAction.Succeed("OfficeSave", log),
            SliceAction.Fail("RunCommands", FailurePolicy.Block, "locked", log),
            SliceAction.Succeed("CloseApps", log)
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        // block 之后 CloseApps 不得执行。
        Assert.Equal(["OfficeSave", "RunCommands"], log);
        Assert.NotNull(result.PrePipeline);
        Assert.Equal(PrePipelineRunStatus.Blocked, result.PrePipeline.Status);
        Assert.Equal(2, result.PrePipeline.Actions.Count);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task FakeActions_ContinueException_IsolatesAndPreservesOrder()
    {
        var log = new List<string>();
        var runner = new PrePipelineRunner([
            SliceAction.Succeed("OfficeSave", log),
            SliceAction.Throw("RunCommands", FailurePolicy.Continue, "boom", log),
            SliceAction.Succeed("CloseApps", log)
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        // 异常被隔离：continue 后仍按顺序执行后续动作。
        Assert.Equal(["OfficeSave", "RunCommands", "CloseApps"], log);
        Assert.NotNull(result.PrePipeline);
        Assert.Equal(3, result.PrePipeline.Actions.Count);
        Assert.False(result.PrePipeline.Actions[1].Succeeded);
        Assert.Equal("RunCommands", result.PrePipeline.Actions[1].ActionName);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task FakeActions_Cancellation_PropagatesWithoutSideEffects()
    {
        var log = new List<string>();
        var runner = new PrePipelineRunner([
            SliceAction.Succeed("OfficeSave", log)
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

        Assert.Empty(log);
        Assert.Empty(power.Requests);
    }

    [Fact]
    public void CompositionRoot_ResolvesPrePipeline_WithOfficeSaveThenRunCommandsThenCloseApps_AndGuardedPower()
    {
        // 安全冒烟（S17/S18/S19）：生产组合根解析出的是挂载 OfficeSave→RunCommands→CloseApps 的流水线 +
        // GuardedPowerService + 惰性 ComOfficeAutomation + 托管 Process 网关
        // （DiagnosticProcessManager/DiagnosticAppWindowManager + CommandRunner），无真实电源 API 暴露；
        // 解析过程不启动任何 Office 进程、不枚举/关闭任何真实进程、不执行任何命令
        // （边界仅在 ExecuteAsync 才触碰）。
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        using var provider = services.BuildServiceProvider();

        var runner = provider.GetRequiredService<IPrePipelineRunner>();
        Assert.IsType<PrePipelineRunner>(runner);
        Assert.NotSame(PrePipelineRunner.Empty, runner);
        Assert.IsType<ComOfficeAutomation>(provider.GetRequiredService<IOfficeAutomation>());
        Assert.IsType<OfficeSaveHelperLauncher>(provider.GetRequiredService<IOfficeSaveHelperLauncher>());
        Assert.IsType<DiagnosticProcessManager>(provider.GetRequiredService<IProcessManager>());
        Assert.IsType<DiagnosticAppWindowManager>(provider.GetRequiredService<IAppWindowManager>());
        Assert.NotNull(provider.GetRequiredService<CloseAppsService>());
        Assert.IsType<CommandRunner>(provider.GetRequiredService<ICommandRunner>());
        Assert.NotNull(provider.GetRequiredService<RunCommandsService>());
        Assert.IsType<GuardedPowerService>(provider.GetRequiredService<IPowerService>());
        Assert.IsType<ShutdownWorkflow>(provider.GetRequiredService<ShutdownWorkflow>());
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

    /// <summary>切片 Fake Action：记录顺序到共享日志，可配置成功/失败/抛异常。</summary>
    private sealed class SliceAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;
        private readonly Action<string> _effect;
        private readonly Func<PrePipelineActionResult> _resultFactory;

        private SliceAction(
            string name,
            FailurePolicy policy,
            Action<string> effect,
            Func<PrePipelineActionResult> resultFactory)
        {
            _name = name;
            _policy = policy;
            _effect = effect;
            _resultFactory = resultFactory;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            _effect(_name);
            return Task.FromResult(_resultFactory());
        }

        public static SliceAction Succeed(string name, List<string> log)
            => new(name, FailurePolicy.Continue, log.Add, () => new PrePipelineActionResult { Succeeded = true });

        public static SliceAction Fail(string name, FailurePolicy policy, string error, List<string> log)
            => new(name, policy, log.Add, () => new PrePipelineActionResult { Succeeded = false, ErrorMessage = error });

        public static SliceAction Throw(string name, FailurePolicy policy, string message, List<string> log)
            => new(name, policy, log.Add, () => throw new InvalidOperationException(message));
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
