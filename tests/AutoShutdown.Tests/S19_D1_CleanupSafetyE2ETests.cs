using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19-D1 清理安全端到端验收。用真实 RunCommandsService + RunCommandsAction + 执行器替身
/// （返回 CleanupNotConfirmed / 抛出 CommandCleanupFailedException）验证：
/// 清理未确认 → 默认 Block → 电源调用 0、后续 CloseApps 不执行（D1-2/D1-4）；
/// 取消（OCE 子类）→ 传播 → 电源调用 0、CloseApps 不执行、不执行下一条命令（D1-5/D1-6/D1-7）。
/// 绝不触发真实关机/重启/休眠，绝不关闭用户应用。
/// </summary>
public sealed class S19_D1_CleanupSafetyE2ETests
{
    private const string ToolPath = @"C:\Tools\Fake.exe";

    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact(Timeout = 5000)]
    public async Task CleanupNotConfirmed_BlocksPipeline_AndPowerZero_AndCloseAppsNotRun()
    {
        var log = new List<string>();
        var runner = Pipeline(new FakeCommandRunner(CleanupNotConfirmedResult()), log);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(Instance(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        Assert.NotNull(result.PrePipeline);
        Assert.Equal(PrePipelineRunStatus.Blocked, result.PrePipeline.Status);
        // OfficeSave 已执行、RunCommands 因清理未确认被阻断、CloseApps 不执行。
        Assert.Equal(["OfficeSave"], log);
        Assert.Equal(2, result.PrePipeline.Actions.Count);
        Assert.False(result.PrePipeline.Actions[1].Succeeded);
        Assert.Equal("RunCommands", result.PrePipeline.Actions[1].ActionName);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 5000)]
    public async Task Cancellation_Propagates_AndPowerZero_AndCloseAppsNotRun_AndNoNextCommand()
    {
        var log = new List<string>();
        var fakeRunner = new FakeCommandRunner(throw_: new CommandCleanupFailedException("cleanup unconfirmed."));
        var runner = Pipeline(fakeRunner, log);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        // CommandCleanupFailedException（OCE 子类）沿取消通道原样传播，不产生副作用。
        await Assert.ThrowsAsync<CommandCleanupFailedException>(
            () => workflow.ExecuteAsync(Instance(), CancellationToken.None));

        Assert.Equal(["OfficeSave"], log); // CloseApps 不执行。
        Assert.Equal(1, fakeRunner.CallCount); // 不执行下一条命令。
        Assert.Empty(power.Requests); // 电源调用 0。
    }

    // ---- 辅助 ----

    private static IPrePipelineRunner Pipeline(ICommandRunner commandRunner, List<string> log)
    {
        var service = new RunCommandsService(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            commandRunner);
        return new PrePipelineRunner(
        [
            new SucceedAction("OfficeSave", log),
            new RunCommandsAction(service),
            new SucceedAction("CloseApps", log)
        ]);
    }

    private static CommandRunResult CleanupNotConfirmedResult() => new()
    {
        Status = CommandRunStatus.CleanupNotConfirmed,
        Message = "timed out; process tree exit not confirmed."
    };

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        RunCommands = new RunCommandsConfig
        {
            DefaultTimeoutSeconds = 30,
            Whitelist = new LocalCommandWhitelist
            {
                Allow = [new WhitelistEntry { Executable = ToolPath, Arguments = ["*"] }]
            },
            Commands = [new CommandConfig { Executable = ToolPath, Arguments = ["x"] }]
        }
    };

    private static TaskInstance Instance() => new()
    {
        InstanceId = InstanceId,
        SourceTaskId = Guid.NewGuid(),
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = Guid.NewGuid(),
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static PowerResult SimulatedResult() => new()
    {
        Outcome = PowerOutcome.Simulated,
        WasSimulated = true,
        Message = "simulated"
    };

    private static ConfigurationLoadResult SuccessResult(AppConfig config) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = config
    };

    private sealed class SucceedAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly List<string> _log;

        public SucceedAction(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => FailurePolicy.Continue;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            _log.Add(_name);
            return Task.FromResult(new PrePipelineActionResult { Succeeded = true });
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

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly CommandRunResult? _result;
        private readonly Exception? _throw;

        public FakeCommandRunner(CommandRunResult result) => _result = result;

        public FakeCommandRunner(Exception throw_) => _throw = throw_;

        public int CallCount { get; private set; }

        public Task<CommandRunResult> RunCommandAsync(CommandSpec command, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_throw is not null)
            {
                return Task.FromException<CommandRunResult>(_throw);
            }

            return Task.FromResult(_result!);
        }
    }
}
