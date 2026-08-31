using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18 C2 服务编排测试。用 IProcessManager / IAppWindowManager / IConfigurationService 替身
/// 覆盖：精确路径/pid 匹配、模糊同名不误杀、优雅关闭、超时、强杀授权、PID 重用、访问拒绝、
/// 窗口/退出竞态、自身/会话/系统关键保护、清单校验与配置不可用 fail-closed。绝不触碰真实进程。
/// </summary>
public sealed class S18_CloseAppsServiceTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    private static readonly DateTimeOffset StartTime =
        new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactPathMatch_ClosesOnlyMatchingPath_NotSimilarName()
    {
        var processes = new FakeProcessManager
        {
            Processes =
            [
                Process(100, @"C:\Windows\System32\notepad.exe"),
                Process(200, @"C:\Windows\System32\notepad2.exe") // 同名前缀、不同路径 → 不关闭
            ]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(@"c:\windows\system32\notepad.exe"))),
            processes,
            window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(CloseAppStatus.ClosedGracefully, result.Status);
        Assert.Single(window.RequestCloseCalls, 100); // 仅向精确匹配的 100 投递，200 不触碰
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task SamePathWithWindowAndBackgroundWorkers_ClosesWindowOnly_WithoutFailingTarget()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100), Process(101), Process(102)]
        };
        var window = new FakeAppWindowManager
        {
            HasMainWindowFunc = pid => pid == 100
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(1, result.MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
    }

    [Fact]
    public async Task PidTarget_MatchesExactly()
    {
        var processes = new FakeProcessManager { Processes = [Process(100), Process(200)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PidConfig(200))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(200, result.ProcessId);
        Assert.Equal(1, result.MatchedCount);
        Assert.Single(window.RequestCloseCalls, 200);
    }

    [Fact]
    public async Task SelfProcess_IsSkippedProtected()
    {
        var processes = new FakeProcessManager
        {
            CurrentProcessId = 100,
            Processes = [Process(100)]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PidConfig(100))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.SkippedProtected, Assert.Single(report.Results).Status);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task NonCurrentSession_IsSkippedProtected()
    {
        var processes = new FakeProcessManager
        {
            CurrentSessionId = 1,
            Processes = [Process(100, sessionId: 0)]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PidConfig(100))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.SkippedProtected, Assert.Single(report.Results).Status);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task SystemCriticalProcess_IsSkippedProtected()
    {
        var processes = new FakeProcessManager
        {
            CurrentSessionId = 1,
            Processes = [Process(100, name: "csrss", sessionId: 1)]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PidConfig(100))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.SkippedProtected, Assert.Single(report.Results).Status);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task AlreadyExited_ReReadReturnsNull_IsSuccess()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100)],
            GetByIdFunc = _ => null // 重新读取时进程已退出
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(CloseAppStatus.AlreadyExited, Assert.Single(report.Results).Status);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task NotRunning_NoMatch_IsSuccess()
    {
        var processes = new FakeProcessManager();
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(CloseAppStatus.NotRunning, result.Status);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task NoWindow_WithoutForceKill_Fails()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager { HasMainWindowFunc = _ => false };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.NoWindow, result.Status);
        Assert.False(result.ForceKillAuthorized);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task NoWindow_WithForceKill_KillsWithExpectedStartTime()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager { HasMainWindowFunc = _ => false };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.ForceKilled, result.Status);
        Assert.True(result.ForceKillAuthorized);
        Assert.Single(window.ForceKillCalls, 100);
        Assert.Equal(StartTime, Assert.Single(window.ForceKillExpectedStartTimes));
    }

    [Fact]
    public async Task GracefulClose_Succeeds()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(CloseAppStatus.ClosedGracefully, Assert.Single(report.Results).Status);
        Assert.Single(window.RequestCloseCalls, 100);
        Assert.Single(window.WaitForExitCalls, 100);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task GracefulTimeout_WithoutForceKill_FailsAndNeverKills()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager { WaitForExitFunc = (_, _, _) => ProcessWaitResult.TimedOut };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.TimedOut, Assert.Single(report.Results).Status);
        Assert.Empty(window.ForceKillCalls); // 未授权强杀 → 绝不 Kill
    }

    [Fact]
    public async Task GracefulTimeout_WithForceKill_Kills()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager { WaitForExitFunc = (_, _, _) => ProcessWaitResult.TimedOut };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(CloseAppStatus.ForceKilled, Assert.Single(report.Results).Status);
        Assert.Single(window.ForceKillCalls, 100);
    }

    [Fact]
    public async Task RequestCloseFalse_ThenExited_IsAlreadyExited()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var exitedCalls = 0;
        var window = new FakeAppWindowManager
        {
            RequestCloseFunc = _ => false,
            GetExitStatusFunc = _ => ++exitedCalls > 1 ? ProcessExitStatus.Exited : ProcessExitStatus.Running
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(CloseAppStatus.AlreadyExited, Assert.Single(report.Results).Status);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task RequestCloseFalse_NotExited_WithoutForceKill_AccessDenied()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            RequestCloseFunc = _ => false,
            GetExitStatusFunc = _ => ProcessExitStatus.Running
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.AccessDenied, Assert.Single(report.Results).Status);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task ForceKill_AccessDenied_IsAccessDenied()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            HasMainWindowFunc = _ => false,
            ForceKillFunc = _ => new ForceKillResult { Status = ForceKillStatus.AccessDenied }
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.AccessDenied, Assert.Single(report.Results).Status);
    }

    [Fact]
    public async Task ForceKill_PidReuseDetected_IsPidReuseDetected()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            HasMainWindowFunc = _ => false,
            ForceKillFunc = _ => new ForceKillResult { Status = ForceKillStatus.PidReuseDetected }
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.PidReuseDetected, Assert.Single(report.Results).Status);
    }

    [Fact]
    public async Task PidReuseAtReRead_RefusesOperation()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, startTime: StartTime)],
            GetByIdFunc = _ => Process(100, startTime: StartTime.AddSeconds(5)) // 启动时间不符 → PID 已重用
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(CloseAppStatus.PidReuseDetected, Assert.Single(report.Results).Status);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task ConfigUnavailable_FailsClosed()
    {
        var service = new CloseAppsService(
            new FakeConfigurationService(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Missing
            }),
            new FakeProcessManager(),
            new FakeAppWindowManager());

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.TargetCount);
        Assert.Contains("configuration", report.Summary);
    }

    [Fact]
    public async Task ConfigLoadThrows_FailsClosed()
    {
        var service = new CloseAppsService(
            new FakeConfigurationService(new InvalidOperationException("boom")),
            new FakeProcessManager(),
            new FakeAppWindowManager());

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("configuration", report.Summary);
    }

    [Fact]
    public async Task ManifestInvalid_FailsClosed()
    {
        var config = PathConfig(NotepadPath);
        config = config with
        {
            CloseApps = config.CloseApps with
            {
                Targets = [new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ProcessId = 1 }]
            }
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(config)),
            new FakeProcessManager { Processes = [Process(100)] },
            new FakeAppWindowManager());

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("invalid", report.Summary);
    }

    [Fact]
    public async Task NullCloseApps_FailsClosed()
    {
        var config = PathConfig(NotepadPath) with { CloseApps = null! };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(config)),
            new FakeProcessManager { Processes = [Process(100)] },
            new FakeAppWindowManager());

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("invalid", report.Summary);
    }

    [Fact]
    public async Task EmptyTargets_Succeeds()
    {
        var config = PathConfig(NotepadPath) with
        {
            CloseApps = new CloseAppsConfig { Targets = [] }
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(config)),
            new FakeProcessManager { Processes = [Process(100)] },
            new FakeAppWindowManager());

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.TargetCount);
        Assert.Empty(report.Results);
        Assert.Empty(report.Summary);
    }

    [Fact]
    public async Task MultiMatch_OneTimedOut_Fails()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100), Process(200)] // 同一路径两个实例
        };
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (pid, _, _) => pid == 100 ? ProcessWaitResult.Exited : ProcessWaitResult.TimedOut // 100 关闭成功，200 超时
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(CloseAppStatus.TimedOut, result.Status);
        Assert.Equal(new[] { 100, 200 }, window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task Failure_SummaryContainsTargetIdAndStatusLabel()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager { WaitForExitFunc = (_, _, _) => ProcessWaitResult.TimedOut };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.Contains("notepad.exe", report.Summary);
        Assert.Contains("timed out", report.Summary);
    }

    [Fact]
    public async Task PreCanceled_ThrowsOperationCanceled()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))),
            processes,
            new FakeAppWindowManager());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CloseAllAsync(cts.Token));
    }

    // ---- S18-D1 返修回归：取消/未知会话/未知退出状态/未确认强杀均 fail-closed ----

    [Fact]
    public async Task UnknownSessionId_IsSkippedProtected_WithoutAnyClose()
    {
        // D1-2：SessionId == -1（未知）不得放行；即使已授权强杀也不得触碰。
        var processes = new FakeProcessManager
        {
            CurrentSessionId = 1,
            Processes = [Process(100, sessionId: -1)]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.SkippedProtected, result.Status);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.WaitForExitCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task ExitStatusUnknown_IsFailure_NotAlreadyExited_NoForceKill()
    {
        // D1-3：退出状态无法确认时，绝不当作已退出，也绝不强杀（fail-closed）。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager { GetExitStatusFunc = _ => ProcessExitStatus.Unknown };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.ExitStatusUnknown, result.Status);
        Assert.NotEqual(CloseAppStatus.AlreadyExited, result.Status);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task ForceKill_ExitNotConfirmed_IsFailure_NotForceKilled()
    {
        // D1-4：强杀后退出未确认时，绝不当作强杀成功。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            HasMainWindowFunc = _ => false,
            ForceKillFunc = _ => new ForceKillResult { Status = ForceKillStatus.ExitNotConfirmed }
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.ExitNotConfirmed, result.Status);
        Assert.NotEqual(CloseAppStatus.ForceKilled, result.Status);
        Assert.Single(window.ForceKillCalls, 100);
    }

    [Fact]
    public async Task CancellationDuringGracefulWait_PropagatesOce_AndNeverForceKills()
    {
        // D1-1：等待期间取消（网关 WaitForExit 抛 OCE）→ 传播 OCE，绝不强杀。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, token) => throw new OperationCanceledException(token)
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CloseAllAsync(CancellationToken.None));

        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task CancellationArrivesDuringWait_BeforeForceKill_PropagatesOce_AndNeverForceKills()
    {
        // D1-1：等待返回超时(false)后、强杀前取消到达 → 强杀前复核取消，传播 OCE，绝不强杀。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var cts = new CancellationTokenSource();
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, _) =>
            {
                cts.Cancel(); // 取消在等待期间到达；等待仍返回超时。
                return ProcessWaitResult.TimedOut;
            }
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CloseAllAsync(cts.Token));

        Assert.Empty(window.ForceKillCalls);
    }

    // ---- S18-D2 返修回归：等待结果三态，未知不再折叠为普通超时 ----

    [Fact]
    public async Task WaitExitUnknown_IsExitStatusUnknown_NotTimedOut_NoForceKill()
    {
        // D2-1：等待异常后复核退出状态为 Unknown → ExitStatusUnknown（fail-closed），
        // 即便已授权强杀也绝不当作 TimedOut、绝不强杀。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, _) => ProcessWaitResult.Unknown
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.ExitStatusUnknown, result.Status);
        Assert.NotEqual(CloseAppStatus.TimedOut, result.Status);
        Assert.NotEqual(CloseAppStatus.ForceKilled, result.Status);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task NormalTimeout_WithoutForceKill_TimedOut_NoForceKill()
    {
        // D2-3a：正常、可确认的超时（TimedOut）未授权强杀 → TimedOut，绝不强杀。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, _) => ProcessWaitResult.TimedOut
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.TimedOut, result.Status);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task NormalTimeout_WithForceKill_EntersForceKillPath()
    {
        // D2-3b：正常超时（TimedOut）已授权强杀 → 进入强杀路径，且强杀前仍复核启动时间。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, _) => ProcessWaitResult.TimedOut
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(CloseAppStatus.ForceKilled, result.Status);
        Assert.Single(window.ForceKillCalls, 100);
        Assert.Equal(StartTime, Assert.Single(window.ForceKillExpectedStartTimes));
    }

    [Fact]
    public async Task CancellationDuringWait_PropagatesOce_NeverReturnsTimeoutOrUnknown()
    {
        // D2-4：等待期间取消 → OCE 传播，绝不返回 Unknown/TimedOut、绝不强杀。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, token) => throw new OperationCanceledException(token)
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            processes, window);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CloseAllAsync(CancellationToken.None));

        Assert.Empty(window.ForceKillCalls);
    }

    // ---- S18-D3 返修回归：等待异常 ≠ 期限届满，服务把完整期限交给网关 ----

    [Fact]
    public async Task Recovery_ServicePassesFullConfiguredTimeout_ToGateway()
    {
        // D3-1 服务侧：服务必须把完整配置的优雅等待期限传给网关，网关才有机会等到期限
        // （而非约 100ms 单次轮询后提前判定超时、提前强杀）。配置 5s → 传给 WaitForExit 的必须是 5s。
        TimeSpan? passed = null;
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, timeout, _) =>
            {
                passed = timeout;
                return ProcessWaitResult.Exited;
            }
        };
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))),
            processes, window);

        await service.CloseAllAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), passed);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task MoreThanSixTargets_AreWaitedConcurrently_InsteadOfAccumulatingTimeouts()
    {
        var paths = Enumerable.Range(1, 7).Select(i => $@"C:\Apps\App{i}.exe").ToArray();
        var processes = new FakeProcessManager
        {
            Processes = paths.Select((path, i) => Process(200 + i, path, name: $"app{i + 1}.exe")).ToList()
        };
        var active = 0;
        var maximum = 0;
        var window = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, _) =>
            {
                var current = Interlocked.Increment(ref active);
                int observed;
                do
                {
                    observed = Volatile.Read(ref maximum);
                } while (current > observed
                    && Interlocked.CompareExchange(ref maximum, current, observed) != observed);
                Thread.Sleep(40);
                Interlocked.Decrement(ref active);
                return ProcessWaitResult.Exited;
            }
        };
        var config = BaseConfig(new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 30,
            Targets = paths.Select(path => new CloseAppsTargetConfig { ExecutablePath = path }).ToArray()
        });
        var service = new CloseAppsService(new FakeConfigurationService(Success(config)), processes, window);

        var result = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.TargetCount);
        Assert.True(maximum > 1, "Multiple application waits should overlap.");
    }

    private static AppConfig PathConfig(string path, bool forceKill = false) =>
        BaseConfig(new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 5,
            Targets = [new CloseAppsTargetConfig { ExecutablePath = path, ForceKillAllowed = forceKill }]
        });

    private static AppConfig PidConfig(int pid) =>
        BaseConfig(new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 5,
            Targets = [new CloseAppsTargetConfig { ProcessId = pid }]
        });

    private static AppConfig BaseConfig(CloseAppsConfig closeApps) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        CloseApps = closeApps
    };

    private static ConfigurationLoadResult Success(AppConfig config) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = config
    };

    private static ProcessSnapshot Process(
        int pid,
        string? path = NotepadPath,
        int sessionId = 1,
        string name = "notepad.exe",
        DateTimeOffset? startTime = null) => new()
    {
        ProcessId = pid,
        ProcessName = name,
        ExecutablePath = path,
        SessionId = sessionId,
        StartTimeUtc = startTime ?? StartTime
    };

    private sealed class FakeConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult? _load;
        private readonly Exception? _exception;

        public FakeConfigurationService(ConfigurationLoadResult load) => _load = load;
        public FakeConfigurationService(Exception exception) => _exception = exception;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_load!);
        }

        public Task<ConfigurationSaveResult> SaveAsync(
            AppConfig config,
            CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
    }

    private sealed class FakeProcessManager : IProcessManager
    {
        public int CurrentProcessId { get; init; } = 1000;
        public int CurrentSessionId { get; init; } = 1;
        public List<ProcessSnapshot> Processes { get; init; } = [];
        public Func<int, ProcessSnapshot?>? GetByIdFunc { get; init; }

        public IReadOnlyList<ProcessSnapshot> EnumerateProcesses() => Processes;

        public ProcessSnapshot? GetProcessById(int processId)
            => GetByIdFunc is not null
                ? GetByIdFunc(processId)
                : Processes.FirstOrDefault(p => p.ProcessId == processId);
    }

    private sealed class FakeAppWindowManager : IAppWindowManager
    {
        public Func<int, bool> HasMainWindowFunc { get; init; } = _ => true;
        public Func<int, bool> RequestCloseFunc { get; init; } = _ => true;
        public Func<int, ProcessExitStatus> GetExitStatusFunc { get; init; } = _ => ProcessExitStatus.Running;
        public Func<int, TimeSpan, CancellationToken, ProcessWaitResult> WaitForExitFunc { get; init; } = (_, _, _) => ProcessWaitResult.Exited;
        public Func<int, ForceKillResult> ForceKillFunc { get; init; } =
            _ => new ForceKillResult { Status = ForceKillStatus.Killed };

        public List<int> RequestCloseCalls { get; } = [];
        public List<int> ForceKillCalls { get; } = [];
        public List<DateTimeOffset> ForceKillExpectedStartTimes { get; } = [];
        public List<int> WaitForExitCalls { get; } = [];
        public List<CancellationToken> WaitForExitTokens { get; } = [];

        public bool HasMainWindow(int processId) => HasMainWindowFunc(processId);

        public bool RequestClose(int processId)
        {
            RequestCloseCalls.Add(processId);
            return RequestCloseFunc(processId);
        }

        public ProcessExitStatus GetExitStatus(int processId) => GetExitStatusFunc(processId);

        public ProcessWaitResult WaitForExit(int processId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            WaitForExitCalls.Add(processId);
            WaitForExitTokens.Add(cancellationToken);
            return WaitForExitFunc(processId, timeout, cancellationToken);
        }

        public ForceKillResult ForceKill(int processId, DateTimeOffset expectedStartTimeUtc)
        {
            ForceKillCalls.Add(processId);
            ForceKillExpectedStartTimes.Add(expectedStartTimeUtc);
            return ForceKillFunc(processId);
        }
    }
}
