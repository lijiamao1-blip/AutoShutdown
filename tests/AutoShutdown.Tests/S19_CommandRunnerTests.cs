using AutoShutdown.App.Infrastructure.Commands;
using AutoShutdown.Core.RunCommands;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19 C2 CommandRunner 真实执行器测试。全部用例在隔离临时目录中用无害的真实（非 shell）
/// 可执行文件（reg.exe / findstr.exe / ping.exe）经 ProcessStartInfo.ArgumentList 逐字面量
/// 传参，验证：退出码 0、非零退出、启动失败、每命令超时 + 进程树终止确认、取消传播、
/// 限量输出截断与「产生预期文件」。绝不触发真实关机/重启/休眠或用户应用关闭。
/// </summary>
public sealed class S19_CommandRunnerTests : IDisposable
{
    private const int MaxOutputCharsPerStream = 8192;

    private static readonly string RegExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
    private static readonly string FindStrExe = Path.Combine(Environment.SystemDirectory, "findstr.exe");
    private static readonly string PingExe = Path.Combine(Environment.SystemDirectory, "ping.exe");

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "AutoShutdown-S19-" + Guid.NewGuid().ToString("N"));

    public S19_CommandRunnerTests() => Directory.CreateDirectory(_tempDir);

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

    [Fact(Timeout = 15000)]
    public async Task ExitCodeZero_IsSuccess()
    {
        var result = await new CommandRunner().RunAsync(
            new CommandSpec
            {
                ExecutablePath = RegExe,
                Arguments = ["query", @"HKCU\Environment"],
                Timeout = TimeSpan.FromSeconds(10)
            },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.Success, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
    }

    [Fact(Timeout = 15000)]
    public async Task NonZeroExit_IsReportedWithCode()
    {
        var result = await new CommandRunner().RunAsync(
            new CommandSpec
            {
                ExecutablePath = RegExe,
                Arguments = ["query", @"HKCU\Software\__AutoShutdown_DefinitelyMissing__"],
                Timeout = TimeSpan.FromSeconds(10)
            },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.NonZeroExit, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact(Timeout = 15000)]
    public async Task NonexistentExecutable_IsLaunchFailed()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.exe");

        var result = await new CommandRunner().RunAsync(
            new CommandSpec { ExecutablePath = missing, Timeout = TimeSpan.FromSeconds(5) },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.LaunchFailed, result.Status);
        Assert.False(result.Succeeded);
    }

    [Fact(Timeout = 20000)]
    public async Task Timeout_TerminatesProcessTree_AndConfirmsExit()
    {
        // 无害挂起命令（loopback ping 约 59 秒），1 秒期限触发超时，进程树被终止且确认退出。
        var result = await new CommandRunner().RunAsync(
            new CommandSpec
            {
                ExecutablePath = PingExe,
                Arguments = ["-n", "60", "127.0.0.1"],
                Timeout = TimeSpan.FromSeconds(1)
            },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.TimedOut, result.Status);
        Assert.False(result.Succeeded);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancellation_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CommandRunner().RunAsync(
                new CommandSpec
                {
                    ExecutablePath = PingExe,
                    Arguments = ["-n", "60", "127.0.0.1"],
                    Timeout = TimeSpan.FromSeconds(30)
                },
                cts.Token));
    }

    [Fact(Timeout = 15000)]
    public async Task ExcessiveOutput_IsTruncatedAndFlagged()
    {
        var bigFile = Path.Combine(_tempDir, "big.txt");
        File.WriteAllLines(bigFile, Enumerable.Range(0, 2000).Select(i => $"aaaaa{i}"));

        var result = await new CommandRunner().RunAsync(
            new CommandSpec
            {
                ExecutablePath = FindStrExe,
                Arguments = ["a", bigFile],
                Timeout = TimeSpan.FromSeconds(10)
            },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.Success, result.Status);
        Assert.True(result.OutputTruncated);
        Assert.True(result.Output.Length <= MaxOutputCharsPerStream + Environment.NewLine.Length);
    }

    [Fact(Timeout = 15000)]
    public async Task AllowedCommand_ProducesExpectedFile_InIsolatedDirectory()
    {
        var file = Path.Combine(_tempDir, "env.reg");

        var result = await new CommandRunner().RunAsync(
            new CommandSpec
            {
                ExecutablePath = RegExe,
                Arguments = ["export", @"HKCU\Environment", file],
                Timeout = TimeSpan.FromSeconds(10)
            },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.Success, result.Status);
        Assert.True(File.Exists(file));
        Assert.Contains("Registry Editor", File.ReadAllText(file));
    }
}
