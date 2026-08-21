using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-CLOSEUI1-D3 配置校验聚焦测试：可执行路径目标必须是完整绝对路径。
/// 相对/drive-relative/根相对路径（如 app.exe、.\app.exe、..\app.exe、folder\app.exe、
/// C:app.exe、\app.exe）在配置校验阶段被拒绝（fail-closed），绝不等到执行时才处理，
/// 绝不按当前工作目录把相对路径转换成可执行关闭目标；合法旧版完整绝对路径继续兼容，
/// PID-only 历史目标语义不变，路径与 PID 并存仍被拒绝。
/// </summary>
public sealed class S_CLOSEUI1_D3_CloseAppsConfigAbsolutePathTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    [Fact]
    public void FullAbsolutePath_IsValid()
    {
        Assert.Empty(CloseAppsTargetList.Validate(ConfigWithPath(NotepadPath)));
    }

    [Theory]
    [InlineData("app.exe")]
    [InlineData(@".\app.exe")]
    [InlineData(@"..\app.exe")]
    [InlineData(@"folder\app.exe")]
    [InlineData("C:app.exe")]
    [InlineData(@"\app.exe")]
    public void RelativeOrPartiallyQualifiedPath_IsRejected(string path)
    {
        var errors = CloseAppsTargetList.Validate(ConfigWithPath(path));

        Assert.Contains(errors, e => e.Contains("fully qualified absolute path"));
    }

    [Fact]
    public void EmptyPath_KeepsExistingRejectionSemantics()
    {
        // 空路径 = 无稳定标识 → 既有「恰取其一」拒绝语义不变（不新增绝对路径错误）。
        var errors = CloseAppsTargetList.Validate(ConfigWithPath(string.Empty));

        Assert.Contains(errors, e => e.Contains("exactly one"));
        Assert.DoesNotContain(errors, e => e.Contains("fully qualified"));
    }

    [Fact]
    public void WhitespaceOnlyPath_KeepsExistingRejectionSemantics()
    {
        var errors = CloseAppsTargetList.Validate(ConfigWithPath("   "));

        Assert.Contains(errors, e => e.Contains("exactly one"));
        Assert.DoesNotContain(errors, e => e.Contains("fully qualified"));
    }

    [Fact]
    public void PidOnlyLegacyTarget_RemainsValid()
    {
        var errors = CloseAppsTargetList.Validate(new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 30,
            Targets = [new CloseAppsTargetConfig { ProcessId = 4242 }]
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void BothPathAndPid_StillRejected()
    {
        var errors = CloseAppsTargetList.Validate(new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 30,
            Targets = [new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ProcessId = 4242 }]
        });

        Assert.Contains(errors, e => e.Contains("exactly one"));
    }

    private static CloseAppsConfig ConfigWithPath(string path) => new()
    {
        GracefulTimeoutSeconds = 30,
        Targets = [new CloseAppsTargetConfig { ExecutablePath = path }]
    };
}

/// <summary>
/// S-CLOSEUI1-D3 执行端聚焦测试：CloseAppsService.Resolve 路径匹配前必须先确认两侧都是
/// 完整绝对路径。相对进程路径即使可按当前 CWD 解析到目标也绝不匹配；drive-relative、
/// 无法规范化（含 NUL）同样绝不匹配；不按进程名/文件名兜底、不回退 PID。大小写与安全
/// 绝对路径文本差异仍按 D2 规则匹配；PID 与启动时间复核、强杀默认关闭及显式授权不变。
/// 全部使用替身，绝不触碰真实进程。
/// </summary>
public sealed class S_CLOSEUI1_D3_CloseAppsExecutionAbsolutePathTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    private static readonly DateTimeOffset StartTime =
        new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AbsoluteTargetAndAbsoluteProcessPath_Match()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, Assert.Single(report.Results).MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task RelativeTarget_ConfigRejected_FailsClosed_EvenIfCwdResolvesToRunningPath()
    {
        // 相对目标 notepad.exe 经 Path.GetFullPath 会按当前 CWD 解析为某绝对路径；构造该 CWD
        // 解析结果就是某个运行进程的路径。配置校验阶段直接拒绝（fail-closed），绝不把相对路径
        // 按当前工作目录转换成可执行关闭目标。
        var resolvedByCwd = Path.GetFullPath("notepad.exe");
        var processes = new FakeProcessManager { Processes = [Process(100, path: resolvedByCwd)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig("notepad.exe"))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.TargetCount);
        Assert.Contains("invalid", report.Summary);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task RelativeProcessPath_EvenIfCwdResolvesToTarget_NeverMatches()
    {
        // 进程枚举路径为相对路径 notepad.exe，其 CWD 解析结果恰好等于目标绝对路径：
        // 旧 EqualsNormalized 会匹配；新绝对路径门槛拒绝相对进程路径，绝不关闭。
        var resolvedByCwd = Path.GetFullPath("notepad.exe");
        var processes = new FakeProcessManager { Processes = [Process(100, path: "notepad.exe")] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(resolvedByCwd))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(CloseAppStatus.NotRunning, result.Status);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task DriveRelativeTarget_ConfigRejected_FailsClosed()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig("C:notepad.exe"))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.TargetCount);
        Assert.Contains("invalid", report.Summary);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task DriveRelativeProcessPath_NeverMatches()
    {
        var processes = new FakeProcessManager { Processes = [Process(100, path: "C:notepad.exe")] };
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
    public async Task UnnormalizableTarget_ConfigRejected_FailsClosed()
    {
        // 完整绝对路径但含 NUL：配置校验阶段同样 fail-closed，不得作为可执行目标。
        var nulPath = "C:\\bad\\notepad" + "\0" + "x\\exe";
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(nulPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.TargetCount);
        Assert.Contains("invalid", report.Summary);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task UnnormalizableProcessPath_WithNul_EvenIfFullyQualified_NeverMatches()
    {
        var nulPath = "C:\\bad\\notepad" + "\0" + "x\\exe";
        var processes = new FakeProcessManager { Processes = [Process(100, path: nulPath)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, Assert.Single(report.Results).MatchedCount);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task RelativeProcessPath_WithMatchingProcessName_NoNameFallback()
    {
        // 相对进程路径且文件名与目标一致（notepad.exe）也绝不按进程名/文件名兜底。
        var resolvedByCwd = Path.GetFullPath("notepad.exe");
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: "notepad.exe", name: "notepad")]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(resolvedByCwd))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, Assert.Single(report.Results).MatchedCount);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task PathTarget_NeverFallsBackToPid()
    {
        // 路径匹配不到时绝不回退 PID：即便存在同 PID 的进程（路径不同）也绝不关闭。
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: @"D:\Other\app.exe")]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, Assert.Single(report.Results).MatchedCount);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task CaseAndSafeTextDifferences_OnAbsolutePaths_StillMatch()
    {
        // 大小写与安全绝对路径文本差异（. .. 正斜杠 首尾空白）仍按 D2 规则匹配。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(
                "  c:\\windows\\system32\\.\\..\\system32\\notepad.EXE  "))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, Assert.Single(report.Results).MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task MatchedCandidate_PidStartTimeRecheck_StillRefusesPidReuse()
    {
        // 路径匹配成功进入关闭候选后，PID + 启动时间复核逻辑不变。
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, startTime: StartTime)],
            GetByIdFunc = _ => Process(100, startTime: StartTime.AddSeconds(7))
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
    public async Task ForceKill_DefaultFalse_AndExplicitAuthorization_Unchanged()
    {
        // 强杀授权默认值与执行条件保持不变。
        Assert.False(new CloseAppsTargetConfig().ForceKillAllowed);

        // 未授权：匹配进程无窗口时绝不强杀，ForceKillAuthorized 如实回显 false。
        var noAuth = new FakeProcessManager { Processes = [Process(100)] };
        var noAuthWindow = new FakeAppWindowManager { HasMainWindowFunc = _ => false };
        var serviceNoAuth = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), noAuth, noAuthWindow);
        var reportNoAuth = await serviceNoAuth.CloseAllAsync(CancellationToken.None);

        Assert.Equal(CloseAppStatus.NoWindow, Assert.Single(reportNoAuth.Results).Status);
        Assert.False(Assert.Single(reportNoAuth.Results).ForceKillAuthorized);
        Assert.Empty(noAuthWindow.ForceKillCalls);

        // 已授权：优雅等待超时后仍进入强杀路径，且强杀前复核启动时间。
        var auth = new FakeProcessManager { Processes = [Process(100)] };
        var authWindow = new FakeAppWindowManager
        {
            WaitForExitFunc = (_, _, _) => ProcessWaitResult.TimedOut
        };
        var serviceAuth = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath, forceKill: true))),
            auth, authWindow);
        var reportAuth = await serviceAuth.CloseAllAsync(CancellationToken.None);

        Assert.Equal(CloseAppStatus.ForceKilled, Assert.Single(reportAuth.Results).Status);
        Assert.Single(authWindow.ForceKillCalls, 100);
        Assert.Equal(StartTime, Assert.Single(authWindow.ForceKillExpectedStartTimes));
    }

    private static AppConfig PathConfig(string path, bool forceKill = false) =>
        BaseConfig(new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 5,
            Targets = [new CloseAppsTargetConfig { ExecutablePath = path, ForceKillAllowed = forceKill }]
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

        public FakeConfigurationService(ConfigurationLoadResult load) => _load = load;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_load!);

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
        public Func<int, ProcessExitStatus> GetExitStatusFunc { get; init; } = _ => ProcessExitStatus.Running;
        public Func<int, TimeSpan, CancellationToken, ProcessWaitResult> WaitForExitFunc { get; init; } = (_, _, _) => ProcessWaitResult.Exited;
        public Func<int, ForceKillResult> ForceKillFunc { get; init; } =
            _ => new ForceKillResult { Status = ForceKillStatus.Killed };

        public List<int> RequestCloseCalls { get; } = [];
        public List<int> ForceKillCalls { get; } = [];
        public List<DateTimeOffset> ForceKillExpectedStartTimes { get; } = [];

        public bool HasMainWindow(int processId) => HasMainWindowFunc(processId);

        public bool RequestClose(int processId)
        {
            RequestCloseCalls.Add(processId);
            return true;
        }

        public ProcessExitStatus GetExitStatus(int processId) => GetExitStatusFunc(processId);

        public ProcessWaitResult WaitForExit(int processId, TimeSpan timeout, CancellationToken cancellationToken)
            => WaitForExitFunc(processId, timeout, cancellationToken);

        public ForceKillResult ForceKill(int processId, DateTimeOffset expectedStartTimeUtc)
        {
            ForceKillCalls.Add(processId);
            ForceKillExpectedStartTimes.Add(expectedStartTimeUtc);
            return ForceKillFunc(processId);
        }
    }
}

/// <summary>
/// S-CLOSEUI1-D3 键级聚焦测试：ExecutablePathKey.EqualsNormalizedAbsolute 是执行端路径匹配
/// 的唯一入口，两侧都必须是完整绝对路径（Path.IsPathFullyQualified），相对/drive-relative/
/// 根相对路径绝不匹配；任一侧无法规范化（含 NUL）也不匹配。大小写与安全绝对路径文本差异
/// 仍按 D2 既有规则匹配。
/// </summary>
public sealed class S_CLOSEUI1_D3_ExecutablePathKeyAbsoluteGateTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    [Fact]
    public void AbsolutePaths_WithCaseAndSafeTextVariants_StillMatch()
    {
        Assert.True(ExecutablePathKey.EqualsNormalizedAbsolute(
            NotepadPath,
            "  c:\\windows\\system32\\.\\..\\system32\\notepad.EXE  "));
    }

    [Fact]
    public void RelativeTarget_EvenIfCwdResolvesToSameAbsolutePath_NeverMatches()
    {
        // 相对目标 notepad.exe 经 Path.GetFullPath 会按当前 CWD 解析为某绝对路径；
        // 该 CWD 解析结果对旧 EqualsNormalized 会匹配，但新绝对路径门槛拒绝相对目标。
        var resolvedByCwd = Path.GetFullPath("notepad.exe");

        Assert.True(ExecutablePathKey.EqualsNormalized("notepad.exe", resolvedByCwd));
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute("notepad.exe", resolvedByCwd));
    }

    [Fact]
    public void RelativeProcessPath_EvenIfCwdResolvesToTarget_NeverMatches()
    {
        var resolvedByCwd = Path.GetFullPath("notepad.exe");

        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(resolvedByCwd, "notepad.exe"));
    }

    [Theory]
    [InlineData("app.exe")]
    [InlineData(@".\app.exe")]
    [InlineData(@"..\app.exe")]
    [InlineData(@"folder\app.exe")]
    [InlineData("C:app.exe")]
    [InlineData(@"\app.exe")]
    public void AnySideNotFullyQualified_NeverMatches(string partiallyQualified)
    {
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(partiallyQualified, NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(NotepadPath, partiallyQualified));
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(partiallyQualified, partiallyQualified));
    }

    [Fact]
    public void UnnormalizablePath_WithNul_NeverMatches_EvenIfFullyQualified()
    {
        var nulPath = "C:\\bad\\notepad" + "\0" + "x\\exe";

        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(nulPath, NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(NotepadPath, nulPath));
    }

    [Fact]
    public void NullOrWhitespace_AnySide_NeverMatches()
    {
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(null, NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(string.Empty, NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute("   ", NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalizedAbsolute(NotepadPath, null));
    }
}

/// <summary>
/// S-CLOSEUI1-D3 源码契约：CloseAppsService 执行路径匹配前存在完整绝对路径门槛；
/// CloseAppsTargetList.Validate 拒绝相对路径目标；生产 CloseApps 代码无手工 8.3 展开、
/// 无名称/PID 兜底、无网络/shell/提权/计划任务调用；OfficeSave→RunCommands→CloseApps
/// 顺序不变；ShutdownWorkflow 仍是唯一 IPowerService 生产出口。
/// </summary>
public sealed class S_CLOSEUI1_D3_SourceContractTests
{
    [Fact]
    public void CloseAppsService_HasAbsolutePathGate_BeforeNormalizedMatch()
    {
        var source = File.ReadAllText(Path.Combine(CoreSourceRoot(), "CloseApps", "CloseAppsService.cs"));

        Assert.Contains("ExecutablePathKey.EqualsNormalizedAbsolute(process.ExecutablePath, path)", source);
        Assert.Contains("S-CLOSEUI1-D3", source);
        // 路径匹配只经 .Where(EqualsNormalizedAbsolute) 完成，绝无名称/PID 兜底分支
        // （不回退 PID 的行为由服务级 PathTarget_NeverFallsBackToPid 用例覆盖）。
    }

    [Fact]
    public void CloseAppsTargetList_Validate_RejectsRelativePathTargets()
    {
        var source = File.ReadAllText(Path.Combine(CoreSourceRoot(), "CloseApps", "CloseAppsTargetList.cs"));

        Assert.Contains("Path.IsPathFullyQualified", source);
        Assert.Contains("fully qualified absolute path", source);
        Assert.Contains("S-CLOSEUI1-D3", source);
    }

    [Fact]
    public void ProductionCloseAppsSources_NoManual8Dot3Expansion_NoNameOrPidFallback()
    {
        foreach (var file in Directory.GetFiles(CoreSourceRoot() + "/CloseApps", "*.cs"))
        {
            var content = File.ReadAllText(file);

            // 无手工 8.3 展开（GetShortPathName/GetLongPathName/fsutil/DllImport 均属测试专属）。
            Assert.DoesNotContain("GetShortPathName", content);
            Assert.DoesNotContain("GetShortPathNameW", content);
            Assert.DoesNotContain("GetLongPathName", content);
            Assert.DoesNotContain("fsutil", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DllImport", content);

            // 产品名/公司名/窗口标题只属「添加时识别信息」，绝不参与执行期匹配/兜底。
            Assert.DoesNotContain("ProductName", content);
            Assert.DoesNotContain("CompanyName", content);
            Assert.DoesNotContain("WindowTitle", content);

            // 无 taskkill/Stop-Process/wmic 终止路径。
            Assert.DoesNotContain("taskkill", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Stop-Process", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wmic", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CloseAppsSources_HaveNoNewNetworkShellElevationOrTaskScheduler()
    {
        foreach (var file in Directory.GetFiles(CoreSourceRoot() + "/CloseApps", "*.cs"))
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("HttpClient", content);
            Assert.DoesNotContain("TcpClient", content);
            Assert.DoesNotContain("Process.Start", content);
            Assert.DoesNotContain("cmd.exe", content);
            Assert.DoesNotContain("powershell", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("runas", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TaskScheduler", content);
            Assert.DoesNotContain("RegisterTask", content);
        }
    }

    [Fact]
    public void ServiceRegistration_Order_StillOfficeSaveRunCommandsCloseApps()
    {
        var registration = File.ReadAllText(Path.Combine(AppSourceRoot(), "AppHost", "ServiceRegistration.cs"));

        Assert.Contains("OfficeSave→RunCommands→CloseApps", registration);
        var office = registration.IndexOf("new OfficeSaveAction", StringComparison.Ordinal);
        var run = registration.IndexOf("new RunCommandsAction", StringComparison.Ordinal);
        var close = registration.IndexOf("new CloseAppsAction", StringComparison.Ordinal);
        Assert.True(office >= 0 && run >= 0 && close >= 0, "三个动作均在注册中。");
        Assert.True(office < run && run < close, "固定顺序 OfficeSave→RunCommands→CloseApps 保持。");
    }

    [Fact]
    public void CloseAppsSources_HaveNoPowerService_AndShutdownWorkflowIsUniqueExit()
    {
        foreach (var file in Directory.GetFiles(CoreSourceRoot() + "/CloseApps", "*.cs"))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("IPowerService", content);
            Assert.DoesNotContain("PowerRequest", content);
        }

        var workflow = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Workflow", "ShutdownWorkflow.cs"));
        Assert.Contains("class ShutdownWorkflow", workflow);
        Assert.Contains("IPowerService", workflow);
    }

    private static string AppSourceRoot() => FindRoot("AutoShutdown.App");

    private static string CoreSourceRoot() => FindRoot("AutoShutdown.Core");

    private static string FindRoot(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", project);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"The {project} source directory was not found.");
    }
}
