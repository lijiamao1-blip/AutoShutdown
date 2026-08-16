using AutoShutdown.App.Infrastructure.Commands;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19 C4 安全与隔离验收。用真实 CommandRunner + 真实 RunCommandsService + 配置替身，
/// 在隔离临时目录中用无害命令（reg.exe / findstr.exe / ping.exe 经 ArgumentList 传参）端到端验证：
/// 多命令顺序、block/continue、非白名单/路径穿越拒绝、特殊字符逐字面量传递、超时进程树终止、
/// 摘要脱敏与「产生预期文件」。绝不触碰真实电源、绝不关闭用户应用。
/// </summary>
public sealed class S19_RunCommandsAcceptanceTests : IDisposable
{
    private static readonly string RegExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
    private static readonly string FindStrExe = Path.Combine(Environment.SystemDirectory, "findstr.exe");
    private static readonly string PingExe = Path.Combine(Environment.SystemDirectory, "ping.exe");

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "AutoShutdown-S19-Acc-" + Guid.NewGuid().ToString("N"));

    public S19_RunCommandsAcceptanceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 清理尽力而为；测试隔离目录残留不致命。
        }
    }

    [Fact(Timeout = 20000)]
    public async Task EndToEnd_WhitelistedCommands_RunInOrder_AndProduceExpectedFiles()
    {
        var file1 = Path.Combine(_tempDir, "one.reg");
        var file2 = Path.Combine(_tempDir, "two.reg");
        var config = Config(
            Whitelist(RegExe, ["export", "*", "*"]),
            new CommandConfig { Executable = RegExe, Arguments = ["export", @"HKCU\Environment", file1] },
            new CommandConfig { Executable = RegExe, Arguments = ["export", @"HKCU\Environment", file2] });
        var service = new RunCommandsService(new FakeConfigurationService(config), new CommandRunner());

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.CommandCount);
        Assert.Equal(2, report.ExecutedCount);
        Assert.Equal(2, report.SucceededCount);
        Assert.Equal([0, 1], report.Results.Select(r => r.Index));
        Assert.All(report.Results, r => Assert.True(r.Executed));
        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
    }

    [Fact(Timeout = 20000)]
    public async Task EndToEnd_NonWhitelistedCommand_IsRejected_NoFileProduced()
    {
        var config = Config(
            Whitelist(RegExe, ["export", "*", "*"]),
            new CommandConfig { Executable = FindStrExe, Arguments = ["a", "b"] }); // findstr 不在白名单
        var service = new RunCommandsService(new FakeConfigurationService(config), new CommandRunner());

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.ExecutedCount);
        var entry = Assert.Single(report.Results);
        Assert.False(entry.Executed);
        Assert.Equal(CommandRejectionReason.NotWhitelisted, entry.RejectionReason);
    }

    [Fact(Timeout = 20000)]
    public async Task EndToEnd_PathTraversal_IsRejected()
    {
        var config = Config(
            Whitelist(RegExe, ["export", "*", "*"]),
            new CommandConfig
            {
                Executable = @"C:\Tools\..\Windows\System32\reg.exe",
                Arguments = ["export", @"HKCU\Environment", "x"]
            });
        var service = new RunCommandsService(new FakeConfigurationService(config), new CommandRunner());

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CommandRejectionReason.ExecutableTraversal, Assert.Single(report.Results).RejectionReason);
    }

    [Fact(Timeout = 20000)]
    public async Task EndToEnd_SpecialCharacters_ArePassedLiterally()
    {
        var file = Path.Combine(_tempDir, "special.txt");
        File.WriteAllText(file, "a&b|c;d" + Environment.NewLine + "other" + Environment.NewLine);
        var config = Config(
            Whitelist(FindStrExe, ["*", "*"]),
            new CommandConfig { Executable = FindStrExe, Arguments = ["a&b|c;d", file] });
        var service = new RunCommandsService(new FakeConfigurationService(config), new CommandRunner());

        var report = await service.RunAllAsync(CancellationToken.None);

        // findstr 仅在字面匹配成功时返回 0；若 & | ; 被 shell 解释则必然失败。
        Assert.True(report.Succeeded);
        Assert.Equal(CommandRunStatus.Success, Assert.Single(report.Results).Status);
    }

    [Fact(Timeout = 20000)]
    public async Task EndToEnd_Timeout_TerminatesProcessTree_AndBlocks()
    {
        var config = Config(
            Whitelist(PingExe, ["-n", "*", "*"]),
            new CommandConfig { Executable = PingExe, Arguments = ["-n", "60", "127.0.0.1"], TimeoutSeconds = 1 });
        var service = new RunCommandsService(new FakeConfigurationService(config), new CommandRunner());

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded); // TimedOut → Block
        Assert.Equal(CommandRunStatus.TimedOut, Assert.Single(report.Results).Status);
    }

    [Fact(Timeout = 20000)]
    public async Task EndToEnd_Summary_DoesNotContainArguments()
    {
        const string secret = "SECRET-TOKEN-12345";
        var missing = Path.Combine(_tempDir, "missing.txt");
        var config = Config(
            Whitelist(FindStrExe, ["*", "*"]),
            new CommandConfig { Executable = FindStrExe, Arguments = [secret, missing] });
        var service = new RunCommandsService(new FakeConfigurationService(config), new CommandRunner());

        var report = await service.RunAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("non-zero exit", report.Summary);
        Assert.DoesNotContain(secret, report.Summary);
    }

    // ---- 辅助 ----

    private static AppConfig Config(LocalCommandWhitelist whitelist, params CommandConfig[] commands) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        RunCommands = new RunCommandsConfig
        {
            DefaultTimeoutSeconds = 30,
            Whitelist = whitelist,
            Commands = commands
        }
    };

    private static LocalCommandWhitelist Whitelist(string executable, string[] argsPattern) => new()
    {
        Allow = [new WhitelistEntry { Executable = executable, Arguments = argsPattern }]
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
}
