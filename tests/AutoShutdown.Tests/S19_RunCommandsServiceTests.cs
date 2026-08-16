using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19 C3 RunCommandsService 编排测试。用 ICommandRunner / IConfigurationService 替身覆盖：
/// 配置不可用 fail-closed、白名单授权（非白名单/参数不匹配拒绝）、逐命令 block/continue、
/// 执行顺序、规范化路径透传、超时/工作目录透传、取消传播与摘要脱敏（不含参数/凭据）。
/// 绝不启动真实进程、绝不触碰电源。
/// </summary>
public sealed class S19_RunCommandsServiceTests
{
    private const string ToolPath = @"C:\Tools\Backup.exe";

    [Fact]
    public async Task ConfigUnavailable_FailsClosed_NoExecution()
    {
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(
            new FakeConfigurationService(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Missing
            }),
            runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.CommandCount);
        Assert.Contains("configuration", report.Summary);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ConfigLoadThrows_FailsClosed()
    {
        var service = new RunCommandsService(
            new FakeConfigurationService(new InvalidOperationException("boom")),
            new FakeCommandRunner());

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("configuration", report.Summary);
    }

    [Fact]
    public async Task NullRunCommands_FailsClosed()
    {
        var config = Config(Section(Whitelist(ToolPath, ["*"]))) with { RunCommands = null! };
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("invalid", report.Summary);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task EmptyCommands_Succeeds_WithoutExecution()
    {
        var config = Config(Section(Whitelist(ToolPath, ["*"])));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.CommandCount);
        Assert.Empty(report.Results);
        Assert.Empty(report.Summary);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task WhitelistedCommand_Succeeds_AndPassesNormalizedPath()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = "C:/Tools/Backup.exe", Arguments = ["one"] }));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.CommandCount);
        Assert.Equal(1, report.ExecutedCount);
        Assert.Equal(1, report.SucceededCount);
        var entry = Assert.Single(report.Results);
        Assert.True(entry.Executed);
        Assert.True(entry.Succeeded);
        Assert.Equal(CommandRunStatus.Success, entry.Status);

        var spec = Assert.Single(runner.Calls);
        Assert.Equal(ToolPath, spec.ExecutablePath); // 正斜杠已规范化、大小写不敏感匹配后透传白名单绝对路径
        Assert.Equal(["one"], spec.Arguments);
    }

    [Fact]
    public async Task NotWhitelisted_IsRejected_FailsClosed()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = @"C:\Tools\Other.exe", Arguments = ["x"] }));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.ExecutedCount);
        var entry = Assert.Single(report.Results);
        Assert.False(entry.Executed);
        Assert.False(entry.Succeeded);
        Assert.Equal(CommandRejectionReason.NotWhitelisted, entry.RejectionReason);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ArgumentMismatch_IsRejected()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["--expected"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["--different"] }));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CommandRejectionReason.ArgumentMismatch, Assert.Single(report.Results).RejectionReason);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task CommandOrder_IsPreserved()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["first"] },
            new CommandConfig { Executable = ToolPath, Arguments = ["second"] }));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.ExecutedCount);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["first"], runner.Calls[0].Arguments);
        Assert.Equal(["second"], runner.Calls[1].Arguments);
        Assert.Equal([0, 1], report.Results.Select(r => r.Index));
    }

    [Fact]
    public async Task BlockFailure_StopsRemainingCommands()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["a"] },
            new CommandConfig { Executable = ToolPath, Arguments = ["b"] }));
        var runner = new FakeCommandRunner(_ => NonZero());
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.ExecutedCount);
        Assert.Single(runner.Calls); // 第二条未被执行
        var entry = Assert.Single(report.Results);
        Assert.Equal(0, entry.Index);
        Assert.Equal(CommandRunStatus.NonZeroExit, entry.Status);
        Assert.False(entry.Succeeded);
    }

    [Fact]
    public async Task ContinueFailure_ContinuesToNextCommand()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["a"], FailurePolicy = "continue" },
            new CommandConfig { Executable = ToolPath, Arguments = ["b"] }));
        var runner = new FakeCommandRunner(spec =>
            spec.Arguments[0] == "a" ? NonZero() : Success());
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded); // Continue 失败不阻断
        Assert.Equal(2, report.ExecutedCount);
        Assert.Equal(1, report.SucceededCount);
        Assert.Equal(2, runner.Calls.Count);
        Assert.False(report.Results[0].Succeeded);
        Assert.True(report.Results[1].Succeeded);
    }

    [Fact]
    public async Task ContinueFailure_OnlyCommand_DoesNotBlock()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["a"], FailurePolicy = "continue" }));
        var runner = new FakeCommandRunner(_ => NonZero());
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded); // 单条 Continue 失败：记录失败但不阻断
        Assert.False(Assert.Single(report.Results).Succeeded);
        Assert.Contains("non-zero exit", report.Summary);
    }

    [Fact]
    public async Task RejectedCommand_WithContinue_ContinuesToNext()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = @"C:\Tools\Other.exe", Arguments = ["x"], FailurePolicy = "continue" },
            new CommandConfig { Executable = ToolPath, Arguments = ["ok"] }));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.ExecutedCount);
        Assert.Equal(2, report.Results.Count);
        Assert.False(report.Results[0].Executed);
        Assert.Equal(CommandRejectionReason.NotWhitelisted, report.Results[0].RejectionReason);
        Assert.True(report.Results[1].Succeeded);
    }

    [Fact]
    public async Task PerCommandTimeoutOverride_IsPassedToRunner()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["x"], TimeoutSeconds = 5 }));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        await service.RunAllAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(runner.Calls).Timeout);
    }

    [Fact]
    public async Task DefaultTimeout_IsUsedWhenOverrideAbsent()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["x"] }) with { DefaultTimeoutSeconds = 30 });
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        await service.RunAllAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(runner.Calls).Timeout);
    }

    [Fact]
    public async Task WorkingDirectory_IsPassedToRunner()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["x"], WorkingDirectory = @"C:\Work" }));
        var runner = new FakeCommandRunner();
        var service = new RunCommandsService(Success(config), runner);

        await service.RunAllAsync(CancellationToken.None);

        Assert.Equal(@"C:\Work", Assert.Single(runner.Calls).WorkingDirectory);
    }

    [Fact]
    public async Task Summary_DoesNotContainArguments_OrSecrets()
    {
        const string secret = "SECRET-TOKEN-12345";
        var config = Config(Section(
            Whitelist(ToolPath, ["--token", "*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["--token", secret] }));
        var runner = new FakeCommandRunner(_ => NonZero());
        var service = new RunCommandsService(Success(config), runner);

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("non-zero exit", report.Summary); // 只有状态标签
        Assert.DoesNotContain(secret, report.Summary);
        Assert.DoesNotContain("--token", report.Summary);
    }

    [Fact]
    public async Task PreCanceled_ThrowsOperationCanceled()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["x"] }));
        var service = new RunCommandsService(Success(config), new FakeCommandRunner());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunAllAsync(cts.Token));
    }

    [Fact]
    public async Task CancellationDuringRun_Propagates_AndNeverRunsNextCommand()
    {
        var config = Config(Section(
            Whitelist(ToolPath, ["*"]),
            new CommandConfig { Executable = ToolPath, Arguments = ["a"] },
            new CommandConfig { Executable = ToolPath, Arguments = ["b"] }));
        var runner = new FakeCommandRunner { ThrowOnRun = new OperationCanceledException() };
        var service = new RunCommandsService(Success(config), runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunAllAsync(CancellationToken.None));

        Assert.Single(runner.Calls); // 取消后绝不执行下一条
    }

    // ---- 辅助 ----

    private static AppConfig Config(RunCommandsConfig runCommands) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        RunCommands = runCommands
    };

    private static RunCommandsConfig Section(
        LocalCommandWhitelist whitelist,
        params CommandConfig[] commands) => new()
    {
        DefaultTimeoutSeconds = 30,
        Whitelist = whitelist,
        Commands = commands
    };

    private static LocalCommandWhitelist Whitelist(string executable, string[] argsPattern) => new()
    {
        Allow = [new WhitelistEntry { Executable = executable, Arguments = argsPattern }]
    };

    private static FakeConfigurationService Success(AppConfig config) => new(config);

    private static CommandRunResult Success() => new()
    {
        Status = CommandRunStatus.Success,
        ExitCode = 0,
        Duration = TimeSpan.FromMilliseconds(1)
    };

    private static CommandRunResult NonZero() => new()
    {
        Status = CommandRunStatus.NonZeroExit,
        ExitCode = 1,
        Duration = TimeSpan.FromMilliseconds(1)
    };

    private sealed class FakeConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult? _load;
        private readonly Exception? _exception;

        public FakeConfigurationService(ConfigurationLoadResult load) => _load = load;
        public FakeConfigurationService(Exception exception) => _exception = exception;
        public FakeConfigurationService(AppConfig config)
            => _load = new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Success, Config = config };

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_load!);
        }

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly Func<CommandSpec, CommandRunResult> _run;
        public List<CommandSpec> Calls { get; } = [];
        public Exception? ThrowOnRun { get; init; }

        public FakeCommandRunner(Func<CommandSpec, CommandRunResult>? run = null)
            => _run = run ?? (_ => Success());

        public Task<CommandRunResult> RunCommandAsync(CommandSpec command, CancellationToken cancellationToken)
        {
            Calls.Add(command);
            if (ThrowOnRun is not null)
            {
                throw ThrowOnRun;
            }

            return Task.FromResult(_run(command));
        }
    }
}
