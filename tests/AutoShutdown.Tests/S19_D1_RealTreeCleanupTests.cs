using System.Diagnostics;
using AutoShutdown.App.Infrastructure.Commands;
using AutoShutdown.Core.RunCommands;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19-D1 真实父子进程树清理回归（D1-1）与真实网关身份匹配（D1-8）。
/// D1-1 用测试专属辅助进程 AutoShutdown.TestTreeHelper（parent 派生 child 后双双挂起）触发真实超时，
/// 经真实 DiagnosticProcessTreeGateway（WMI 父子关系 + PID/启动时间身份）枚举并确认整树退出。
/// D1-8 直接验证 DecideLiveness 纯函数：PID 复用不得误杀新进程、不得把新进程算作原后代存活。
/// 绝不触碰真实电源、绝不关闭用户应用。
/// </summary>
public sealed class S19_D1_RealTreeCleanupTests : IDisposable
{
    private static readonly string HelperExe =
        Path.Combine(AppContext.BaseDirectory, "AutoShutdown.TestTreeHelper.exe");

    public S19_D1_RealTreeCleanupTests() => KillLeftoverHelpers();

    public void Dispose() => KillLeftoverHelpers();

    [Fact(Timeout = 30000)]
    public async Task RealTree_Timeout_TerminatesParentAndChild_AndReturnsTimedOut()
    {
        Assert.True(File.Exists(HelperExe), "TestTreeHelper EXE was not copied to the test output directory.");

        var runner = new CommandRunner(); // 真实 DiagnosticProcessTreeGateway（WMI 枚举父子树）。

        var result = await runner.RunCommandAsync(
            new CommandSpec
            {
                ExecutablePath = HelperExe,
                Arguments = ["parent"],
                Timeout = TimeSpan.FromSeconds(1)
            },
            CancellationToken.None);

        // 子进程确已派生（父进程挂起前打印 child-pid），真实网关识别父子树并确认整树退出才返回 TimedOut。
        // 诊断：失败时把脱敏清理原因带进断言消息（只含类别，无参数/路径/凭据）。
        Assert.Contains("child-pid:", result.Output);
        Assert.True(
            result.Status == CommandRunStatus.TimedOut,
            $"Status={result.Status}, CleanupReason={result.CleanupFailureReason}, Output={result.Output}");
        Assert.False(result.Succeeded);
    }

    // ---- D1-8：身份匹配（PID 复用保护 / fail-closed） ----

    [Fact]
    public void PidReuse_StartTimeMismatch_IsExited_NotAlive()
    {
        var identity = Identity(5, T(1));
        var liveness = DiagnosticProcessTreeGateway.DecideLiveness(
            identity, processExists: true, currentStartTimeUtc: T(2), currentStartTimeKnown: true);

        // PID 被系统复用为新进程（启动时间不同）：原进程已退出，绝不判为存活、绝不误杀新进程。
        Assert.Equal(ProcessLiveness.Exited, liveness);
    }

    [Fact]
    public void SameStartTime_IsAlive()
    {
        var identity = Identity(5, T(1));
        var liveness = DiagnosticProcessTreeGateway.DecideLiveness(
            identity, processExists: true, currentStartTimeUtc: T(1), currentStartTimeKnown: true);

        Assert.Equal(ProcessLiveness.Alive, liveness);
    }

    [Fact]
    public void PidGone_IsExited()
    {
        var liveness = DiagnosticProcessTreeGateway.DecideLiveness(
            Identity(5, T(1)), processExists: false, currentStartTimeUtc: default, currentStartTimeKnown: false);

        Assert.Equal(ProcessLiveness.Exited, liveness);
    }

    [Fact]
    public void UnknownSnapshotStartTime_IsUnknown()
    {
        var identity = Identity(5, default); // 快照时启动时间未读取到：身份不明确。
        var liveness = DiagnosticProcessTreeGateway.DecideLiveness(
            identity, processExists: true, currentStartTimeUtc: T(1), currentStartTimeKnown: true);

        Assert.Equal(ProcessLiveness.Unknown, liveness);
    }

    [Fact]
    public void UnknownCurrentStartTime_IsUnknown()
    {
        var liveness = DiagnosticProcessTreeGateway.DecideLiveness(
            Identity(5, T(1)), processExists: true, currentStartTimeUtc: default, currentStartTimeKnown: false);

        Assert.Equal(ProcessLiveness.Unknown, liveness);
    }

    // ---- 辅助 ----

    private static ProcessIdentity Identity(int processId, DateTimeOffset startTimeUtc) => new()
    {
        ProcessId = processId,
        StartTimeUtc = startTimeUtc
    };

    private static DateTimeOffset T(int second) => new(2024, 1, 1, 0, 0, second, TimeSpan.Zero);

    private static void KillLeftoverHelpers()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("AutoShutdown.TestTreeHelper"))
            {
                using (process)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // 测试清理尽力而为；无残留进程为理想状态。
        }
    }
}
