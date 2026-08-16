using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19 C3 RunCommandsAction 动作层单元测试。验证动作契约（名称/默认 Block 策略）、上下文校验、
/// 报告到 PrePipelineActionResult 的映射（成功清空错误文本、失败携带脱敏摘要）、取消传播。
/// 通过 RunCommandsService + 配置/执行器替身验证，绝不启动真实进程、绝不触碰电源。
/// </summary>
public sealed class S19_RunCommandsActionTests
{
    private const string ToolPath = @"C:\Tools\Backup.exe";

    [Fact]
    public void Defaults_NameIsRunCommands_AndPolicyIsBlock()
    {
        var action = new RunCommandsAction(Service([]));

        Assert.Equal("RunCommands", action.Name);
        Assert.Equal(FailurePolicy.Block, action.FailurePolicy);
    }

    [Fact]
    public void ContinuePolicyVariant_ExposesContinue()
    {
        var action = new RunCommandsAction(Service([]), FailurePolicy.Continue);

        Assert.Equal(FailurePolicy.Continue, action.FailurePolicy);
    }

    [Fact]
    public async Task Success_MapsToSucceededWithEmptyError()
    {
        var action = new RunCommandsAction(Service([]));

        var result = await action.ExecuteAsync(Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.ErrorMessage);
    }

    [Fact]
    public async Task Failure_MapsSummaryToErrorMessage()
    {
        var action = new RunCommandsAction(Service(
            [new CommandConfig { Executable = ToolPath, Arguments = ["x"] }],
            nonZero: true));

        var result = await action.ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("0: non-zero exit", result.ErrorMessage);
    }

    [Fact]
    public void NullService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RunCommandsAction(null!));
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        var action = new RunCommandsAction(Service([]));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var action = new RunCommandsAction(Service(
            [new CommandConfig { Executable = ToolPath, Arguments = ["x"] }]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => action.ExecuteAsync(Context(), cts.Token));
    }

    private static RunCommandsService Service(CommandConfig[] commands, bool nonZero = false)
        => new(
            new FakeConfigurationService(ConfigWith(commands)),
            new FakeCommandRunner(nonZero));

    private static AppConfig ConfigWith(CommandConfig[] commands) => new()
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
            Commands = commands
        }
    };

    private static PrePipelineContext Context() => new()
    {
        InstanceId = Guid.NewGuid(),
        SourceTaskId = Guid.NewGuid(),
        Action = PowerAction.Shutdown,
        ScheduledFireTime = DateTimeOffset.UtcNow
    };

    private sealed class FakeConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public FakeConfigurationService(AppConfig config) => _config = config;

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

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly bool _nonZero;

        public FakeCommandRunner(bool nonZero) => _nonZero = nonZero;

        public Task<CommandRunResult> RunAsync(CommandSpec command, CancellationToken cancellationToken)
            => Task.FromResult(_nonZero
                ? new CommandRunResult { Status = CommandRunStatus.NonZeroExit, ExitCode = 1 }
                : new CommandRunResult { Status = CommandRunStatus.Success, ExitCode = 0 });
    }
}
