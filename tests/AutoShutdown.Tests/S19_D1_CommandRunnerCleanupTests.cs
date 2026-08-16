using AutoShutdown.App.Infrastructure.Commands;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.RunCommands;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19-D1 CommandRunner 清理边界聚焦回归（单元层：真实 Runner + 真实无害挂起命令 + 进程树网关替身）。
/// 用 ping.exe（loopback，经 ArgumentList）触发真实超时/取消，网关替身控制整树枚举与存活复核结果，
/// 验证：整树退出确认（D1-2/D1-3）、清理失败 fail-closed（D1-4）、取消清理成功→纯 OCE（D1-5）、
/// 取消清理未确认/清理抛异常→可识别 OCE 子类 CommandCleanupFailedException（D1-6/D1-7）。
/// 绝不触发真实关机/重启/休眠或用户应用关闭。
/// </summary>
public sealed class S19_D1_CommandRunnerCleanupTests
{
    private static readonly string PingExe = Path.Combine(Environment.SystemDirectory, "ping.exe");

    // ---- 超时路径：整树退出确认（D1-2 / D1-3） ----

    [Fact(Timeout = 20000)]
    public async Task Timeout_ChildUnknown_ReturnsCleanupNotConfirmed()
    {
        var gateway = new FakeProcessTreeGateway
        {
            SnapshotResult = [Identity(1), Identity(2)],
            Liveness = id => id.ProcessId == 1 ? ProcessLiveness.Exited : ProcessLiveness.Unknown
        };
        var runner = new CommandRunner(gateway);

        var result = await runner.RunCommandAsync(
            new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(1) },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.CleanupNotConfirmed, result.Status);
        Assert.False(result.Succeeded);
    }

    [Fact(Timeout = 20000)]
    public async Task Timeout_RootExitedButChildAlive_ReturnsCleanupNotConfirmed_NotTimedOut()
    {
        // 根进程已退出（HasExited=true）但后代仍存活：绝不允许仅凭根退出就误报 TimedOut。
        var gateway = new FakeProcessTreeGateway
        {
            SnapshotResult = [Identity(1), Identity(2)],
            Liveness = id => id.ProcessId == 1 ? ProcessLiveness.Exited : ProcessLiveness.Alive
        };
        var runner = new CommandRunner(gateway);

        var result = await runner.RunCommandAsync(
            new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(1) },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.CleanupNotConfirmed, result.Status);
        Assert.NotEqual(CommandRunStatus.TimedOut, result.Status);
        Assert.Contains("not confirmed", result.Message);
    }

    // ---- 超时路径：清理失败 fail-closed（D1-4） ----

    [Theory(Timeout = 20000)]
    [InlineData(CleanupFailureKind.SnapshotAggregateException)]
    [InlineData(CleanupFailureKind.SnapshotEnumerationFailure)]
    [InlineData(CleanupFailureKind.LivenessAccessDenied)]
    [InlineData(CleanupFailureKind.DescendantNeverExits)]
    public async Task Timeout_CleanupFailure_ReturnsCleanupNotConfirmed(CleanupFailureKind kind)
    {
        var runner = new CommandRunner(Gateway(kind));

        var result = await runner.RunCommandAsync(
            new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(1) },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.CleanupNotConfirmed, result.Status);
        Assert.False(result.Succeeded);
    }

    // ---- 取消路径：清理成功 → 纯 OCE（D1-5） ----

    [Fact(Timeout = 20000)]
    public async Task Cancellation_CleanupConfirmed_PropagatesPlainOperationCanceledException()
    {
        var gateway = new FakeProcessTreeGateway
        {
            SnapshotResult = [Identity(1), Identity(2)],
            Liveness = _ => ProcessLiveness.Exited
        };
        var runner = new CommandRunner(gateway);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(30) },
                cts.Token));

        // 清理已确认：取消以「普通 OCE」传播，不得替换为 CommandCleanupFailedException。
        Assert.IsNotType<CommandCleanupFailedException>(ex);
    }

    // ---- 取消路径：清理未确认 → CommandCleanupFailedException（D1-6） ----

    [Fact(Timeout = 20000)]
    public async Task Cancellation_CleanupNotConfirmed_ThrowsCommandCleanupFailedException()
    {
        var gateway = new FakeProcessTreeGateway
        {
            SnapshotResult = [Identity(1), Identity(2)],
            Liveness = id => id.ProcessId == 1 ? ProcessLiveness.Exited : ProcessLiveness.Alive
        };
        var runner = new CommandRunner(gateway);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<CommandCleanupFailedException>(() =>
            runner.RunCommandAsync(
                new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(30) },
                cts.Token));

        // 仍是 OCE（取消通道），并携带取消令牌。
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    // ---- 取消路径：清理抛异常 → CommandCleanupFailedException（D1-7） ----

    [Theory(Timeout = 20000)]
    [InlineData(CleanupFailureKind.SnapshotEnumerationFailure)]
    [InlineData(CleanupFailureKind.LivenessAccessDenied)]
    public async Task Cancellation_CleanupThrows_ThrowsCommandCleanupFailedException(CleanupFailureKind kind)
    {
        var runner = new CommandRunner(Gateway(kind));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<CommandCleanupFailedException>(() =>
            runner.RunCommandAsync(
                new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(30) },
                cts.Token));

        // 保留原始清理异常为 inner、保留取消令牌；绝不携带参数/secret/token/完整输出。
        Assert.NotNull(ex.InnerException);
        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    // ---- 辅助 ----

    public enum CleanupFailureKind
    {
        SnapshotAggregateException,
        SnapshotEnumerationFailure,
        LivenessAccessDenied,
        DescendantNeverExits
    }

    private static ProcessIdentity Identity(int processId) => new()
    {
        ProcessId = processId,
        StartTimeUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static FakeProcessTreeGateway Gateway(CleanupFailureKind kind) => kind switch
    {
        CleanupFailureKind.SnapshotAggregateException => new FakeProcessTreeGateway
        {
            SnapshotException = new AggregateException("tree snapshot failed.")
        },
        CleanupFailureKind.SnapshotEnumerationFailure => new FakeProcessTreeGateway
        {
            SnapshotException = new InvalidOperationException("process tree enumeration failed.")
        },
        CleanupFailureKind.LivenessAccessDenied => new FakeProcessTreeGateway
        {
            SnapshotResult = [Identity(1), Identity(2)],
            LivenessException = _ => new UnauthorizedAccessException("access denied.")
        },
        CleanupFailureKind.DescendantNeverExits => new FakeProcessTreeGateway
        {
            SnapshotResult = [Identity(1), Identity(2)],
            Liveness = id => id.ProcessId == 1 ? ProcessLiveness.Exited : ProcessLiveness.Alive
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed class FakeProcessTreeGateway : IProcessTreeGateway
    {
        public IReadOnlyList<ProcessIdentity> SnapshotResult { get; init; } = [];
        public Exception? SnapshotException { get; init; }
        public Func<ProcessIdentity, ProcessLiveness> Liveness { get; init; } = _ => ProcessLiveness.Exited;
        public Func<ProcessIdentity, Exception>? LivenessException { get; init; }

        public IReadOnlyList<ProcessIdentity> SnapshotTree(int rootProcessId)
        {
            if (SnapshotException is not null)
            {
                throw SnapshotException;
            }

            return SnapshotResult;
        }

        public ProcessLiveness CheckLiveness(ProcessIdentity identity)
        {
            if (LivenessException is not null)
            {
                throw LivenessException(identity);
            }

            return Liveness(identity);
        }
    }
}
