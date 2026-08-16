using AutoShutdown.App.Infrastructure.Commands;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.RunCommands;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19-D2A 进程树网关快照失败契约聚焦回归。验证：
/// 1) 单条 WMI 记录无法解析 ProcessId/ParentProcessId → 整次快照失败（不再 continue 静默跳过，
///    绝不生成不完整快照）；2) 超时清理快照失败 → CleanupNotConfirmed（脱敏原因 SnapshotFailed）；
/// 3) 外部取消快照失败 → CommandCleanupFailedException : OperationCanceledException（仍 OCE 通道）；
/// 4) 快照正常完成 → 进入既有 Kill/确认路径（TimedOut）。
/// 测试用网关替身 + 无害 ping 经 ArgumentList 快速确定，绝不真实等待、绝不触碰真实电源。
/// </summary>
public sealed class S19_D2A_ProcessTreeGatewayTests
{
    private static readonly string PingExe = Path.Combine(Environment.SystemDirectory, "ping.exe");

    // ---- 单条 WMI 记录解析契约 ----

    [Fact]
    public void ParseProcessRecord_ValidValues_ReturnsIdentifiers()
    {
        var (processId, parentProcessId) = DiagnosticProcessTreeGateway.ParseProcessRecord(123, 45);

        Assert.Equal(123, processId);
        Assert.Equal(45, parentProcessId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void ParseProcessRecord_InvalidProcessId_ThrowsSnapshotFailure(object? processIdValue)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DiagnosticProcessTreeGateway.ParseProcessRecord(processIdValue, 45));

        Assert.Contains("snapshot failed", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void ParseProcessRecord_InvalidParentProcessId_ThrowsSnapshotFailure(object? parentProcessIdValue)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DiagnosticProcessTreeGateway.ParseProcessRecord(123, parentProcessIdValue));

        Assert.Contains("snapshot failed", exception.Message);
    }

    // ---- 超时清理：快照失败 → CleanupNotConfirmed（含 SnapshotFailed 脱敏原因） ----

    [Fact(Timeout = 20000)]
    public async Task Timeout_SnapshotFailure_ReturnsCleanupNotConfirmed_WithSnapshotFailedReason()
    {
        var runner = new CommandRunner(FailingGateway());

        var result = await runner.RunCommandAsync(
            new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(1) },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.CleanupNotConfirmed, result.Status);
        Assert.Equal(CommandCleanupFailureReason.SnapshotFailed, result.CleanupFailureReason);
        Assert.False(result.Succeeded);
    }

    // ---- 外部取消：快照失败 → CommandCleanupFailedException : OperationCanceledException ----

    [Fact(Timeout = 20000)]
    public async Task Cancellation_SnapshotFailure_ThrowsCommandCleanupFailedException()
    {
        var runner = new CommandRunner(FailingGateway());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var exception = await Assert.ThrowsAsync<CommandCleanupFailedException>(() =>
            runner.RunCommandAsync(
                new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(30) },
                cts.Token));

        // 仍满足 OperationCanceledException（取消通道），保留取消令牌。
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
        Assert.NotNull(exception.InnerException);
    }

    // ---- 快照正常完成 → 进入既有 Kill/确认路径（TimedOut） ----

    [Fact(Timeout = 20000)]
    public async Task Timeout_SnapshotSucceeds_EntersKillAndConfirmPath_TimedOut()
    {
        var gateway = new StubProcessTreeGateway
        {
            SnapshotResult = [Identity(1)],
            Liveness = _ => ProcessLiveness.Exited
        };
        var runner = new CommandRunner(gateway);

        var result = await runner.RunCommandAsync(
            new CommandSpec { ExecutablePath = PingExe, Arguments = ["-n", "60", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(1) },
            CancellationToken.None);

        Assert.Equal(CommandRunStatus.TimedOut, result.Status);
        Assert.False(result.Succeeded);
    }

    // ---- 辅助 ----

    private static ProcessIdentity Identity(int processId) => new()
    {
        ProcessId = processId,
        StartTimeUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static StubProcessTreeGateway FailingGateway() => new()
    {
        SnapshotException = new InvalidOperationException("process tree enumeration failed.")
    };

    private sealed class StubProcessTreeGateway : IProcessTreeGateway
    {
        public Exception? SnapshotException { get; init; }

        public IReadOnlyList<ProcessIdentity> SnapshotResult { get; init; } = [];

        public Func<ProcessIdentity, ProcessLiveness> Liveness { get; init; } = _ => ProcessLiveness.Exited;

        public IReadOnlyList<ProcessIdentity> SnapshotTree(int rootProcessId)
        {
            if (SnapshotException is not null)
            {
                throw SnapshotException;
            }

            return SnapshotResult;
        }

        public ProcessLiveness CheckLiveness(ProcessIdentity identity) => Liveness(identity);
    }
}
