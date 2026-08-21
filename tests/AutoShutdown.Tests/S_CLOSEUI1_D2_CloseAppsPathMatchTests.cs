using System.IO;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-CLOSEUI1-D2 聚焦测试：执行端路径规范化一致性。CloseAppsService.Resolve 改为与保存端
/// 一致，统一经 ExecutablePathKey.EqualsNormalized（Path.GetFullPath 文本规范化 + 大小写
/// 不敏感）匹配目标路径与进程枚举路径；任一侧为空/非法/含 NUL/无法规范化时 fail-closed，
/// 绝不按进程名/文件名/前缀兜底、不回退 PID。本测试全部使用替身，绝不触碰真实进程。
/// </summary>
public sealed class S_CLOSEUI1_D2_CloseAppsPathMatchTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    private static readonly DateTimeOffset StartTime =
        new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactAbsolutePath_OldStyleConfig_StillMatches()
    {
        // 旧配置中的合法绝对路径（D1 保存端规范化前的既有格式）仍向后兼容。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(CloseAppStatus.ClosedGracefully, result.Status);
        Assert.Single(window.RequestCloseCalls, 100);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task CaseOnlyDifference_Matches()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(@"c:\WINDOWS\system32\notepad.EXE"))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, Assert.Single(report.Results).MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
    }

    [Fact]
    public async Task DotSegment_Matches()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(@"C:\Windows\System32\.\notepad.exe"))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, Assert.Single(report.Results).MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
    }

    [Fact]
    public async Task DotDotSegment_Matches()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(@"C:\Windows\System32\..\System32\notepad.exe"))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, Assert.Single(report.Results).MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
    }

    [Fact]
    public async Task ForwardSeparators_OnThisWindows_GetFullPathNormalizes_Matches()
    {
        // 如实记录当前环境（Windows 11 / .NET 8）：Path.GetFullPath 把正斜杠归一为反斜杠，
        // 因此正反目录分隔符差异按 Windows 规则正确处理（无手工字符串替换）。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(@"C:/Windows/System32/notepad.exe"))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, Assert.Single(report.Results).MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
    }

    [Fact]
    public async Task TargetWithSurroundingWhitespace_Matches()
    {
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig("   " + NotepadPath + "   "))),
            processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, Assert.Single(report.Results).MatchedCount);
        Assert.Single(window.RequestCloseCalls, 100);
    }

    [Fact]
    public async Task CompletelyDifferentAbsolutePath_NeverMatches()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: @"C:\Windows\System32\winver.exe")]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(CloseAppStatus.NotRunning, result.Status);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task SameFileName_DifferentDirectory_NeverMatches()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: @"D:\Other\notepad.exe")]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, Assert.Single(report.Results).MatchedCount);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task PrefixOnly_NeverMatches()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: @"C:\Windows\System32\notepad2.exe")] // 前缀相同、路径不同
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, Assert.Single(report.Results).MatchedCount);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task NullProcessExecutablePath_NeverMatches()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: null)]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, Assert.Single(report.Results).MatchedCount);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task UnnormalizableProcessPath_WithNul_NeverMatches()
    {
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: "C:\\bad\\notepad" + "\0" + "x\\exe")]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, Assert.Single(report.Results).MatchedCount);
        Assert.Empty(window.RequestCloseCalls);
    }

    [Fact]
    public async Task UnnormalizablePath_WithMatchingProcessName_NoNameFallback()
    {
        // 规范化失败（含 NUL）时绝不按进程名兜底：进程名与目标文件名相同也不匹配。
        var processes = new FakeProcessManager
        {
            Processes = [Process(100, path: "C:\\bad\\notepad" + "\0" + "x\\exe", name: "notepad.exe")]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(CloseAppStatus.NotRunning, result.Status);
        Assert.Empty(window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task MultipleProcesses_OnlyNormalizedExactPathInstances_AreReturned()
    {
        // 同一路径可能匹配多个运行实例（既有语义不变）；仅规范化后精确相等的实例入选。
        var processes = new FakeProcessManager
        {
            Processes =
            [
                Process(100, path: NotepadPath),
                Process(200, path: @"c:\windows\system32\notepad.EXE"), // 大小写不同 → 等价
                Process(300, path: @"C:\Windows\System32\notepad2.exe") // 相似名 → 不等价
            ]
        };
        var window = new FakeAppWindowManager();
        var service = new CloseAppsService(
            new FakeConfigurationService(Success(PathConfig(NotepadPath))), processes, window);

        var report = await service.CloseAllAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        var result = Assert.Single(report.Results);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(new[] { 100, 200 }, window.RequestCloseCalls);
        Assert.DoesNotContain(300, window.RequestCloseCalls);
        Assert.Empty(window.ForceKillCalls);
    }

    [Fact]
    public async Task MatchedCandidate_StartTimeRecheck_StillRefusesPidReuse()
    {
        // 路径匹配成功进入关闭候选后，PID + 启动时间复核逻辑保持不变：
        // 重新读取启动时间不符 → PidReuseDetected，绝不投递窗口消息/强杀。
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
    public async Task ForceKillAuthorization_DefaultFalse_AndConditionUnchanged()
    {
        // 强杀授权默认值与执行条件保持不变：未配置 ForceKillAllowed → 默认 false，
        // 匹配进程无窗口时绝不强杀，ForceKillAuthorized 如实回显 false。
        Assert.False(new CloseAppsTargetConfig().ForceKillAllowed);

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
    public async Task ForceKillAuthorization_True_StillForceKillsOnWindowTimeout()
    {
        // 已授权强杀时执行条件不变：优雅等待超时且已授权 → 强杀路径仍触发，且强杀前复核启动时间。
        var processes = new FakeProcessManager { Processes = [Process(100)] };
        var window = new FakeAppWindowManager { WaitForExitFunc = (_, _, _) => ProcessWaitResult.TimedOut };
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

/// <summary>
/// S-CLOSEUI1-D2：ExecutablePathKey.Normalize / EqualsNormalized 在当前 Windows 环境的真实
/// 行为。如实记录 .NET Path.GetFullPath 的规范化结果（正斜杠→反斜杠、`.`/`..` 折叠、裁剪
/// 空白、NUL 抛错 fail-closed），验证执行端沿用同一套规则即可正确匹配。
/// </summary>
public sealed class S_CLOSEUI1_D2_ExecutablePathKeyNormalizationTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    [Fact]
    public void Normalize_ForwardSeparators_ProduceBackslashPath_OnThisWindows()
    {
        // 当前环境（Windows 11 / .NET 8 / net8.0-windows）：GetFullPath 把正斜杠归一为反斜杠。
        var normalized = ExecutablePathKey.Normalize(@"C:/Windows/System32/notepad.exe");
        Assert.NotNull(normalized);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", normalized);
    }

    [Fact]
    public void EqualsNormalized_AllTextVariants_AreEqual()
    {
        // 大小写 + 首尾空白 + `.` + `..` + 正反分隔符 组合后仍等价。
        Assert.True(ExecutablePathKey.EqualsNormalized(
            NotepadPath,
            "  c:\\windows\\System32\\.\\..\\system32\\notepad.EXE  "));
    }

    [Fact]
    public void EqualsNormalized_RepeatedSeparators_CollapsedByGetFullPath()
    {
        // GetFullPath 折叠重复分隔符：C:\Windows\\System32\\notepad.exe → C:\Windows\System32\notepad.exe。
        Assert.True(ExecutablePathKey.EqualsNormalized(
            NotepadPath,
            @"C:\Windows\\System32\\notepad.exe"));
    }

    [Fact]
    public void EqualsNormalized_TrailingDotSegment_StrippedByGetFullPath()
    {
        // GetFullPath 在 Windows 上会去除末段尾点（Win32 语义）：notepad.exe. → notepad.exe。
        Assert.True(ExecutablePathKey.EqualsNormalized(
            NotepadPath,
            @"C:\Windows\System32\notepad.exe."));
    }

    [Fact]
    public void EqualsNormalized_AnySideNullOrWhitespace_False()
    {
        Assert.False(ExecutablePathKey.EqualsNormalized(null, NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalized(string.Empty, NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalized("   ", NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalized(NotepadPath, null));
    }

    [Fact]
    public void EqualsNormalized_AnySideWithNul_False()
    {
        var nulPath = "C:\\bad\\notepad" + "\0" + "x\\exe";
        Assert.False(ExecutablePathKey.EqualsNormalized(nulPath, NotepadPath));
        Assert.False(ExecutablePathKey.EqualsNormalized(NotepadPath, nulPath));
    }

    [Fact]
    public void EqualsNormalized_ExistingShort8Dot3Segment_ExpandedByGetFullPath_OnThisWindows()
    {
        // 如实记录当前环境（Windows 11 / .NET 8.0.30 / net8.0-windows）：Path.GetFullPath 会对
        // 真实存在的 8.3 短名段调用 Win32 展开（GetLongPathName），故 C:\PROGRA~1\App\app.exe
        // 与 C:\Program Files\App\app.exe 归一化后相等。这并非本阶段手工 8.3 展开，而是既有
        // ExecutablePathKey.Normalize 在 Windows 上的既有行为；保存端与执行端因此自动一致，
        // 原「8.3 与长路径必然匹配不到」的假设在当前环境不成立（详见结果记录 §5/§7）。
        Assert.True(ExecutablePathKey.EqualsNormalized(
            @"C:\PROGRA~1\App\app.exe",
            @"C:\Program Files\App\app.exe"));
    }
}

/// <summary>
/// S-CLOSEUI1-D2 源码契约：Resolve 只经 ExecutablePathKey.EqualsNormalized 匹配、
/// 无进程名兜底；固定顺序 OfficeSave→RunCommands→CloseApps 不变；
/// ShutdownWorkflow 仍是唯一 IPowerService 生产出口（CloseApps 不引入电源依赖）。
/// </summary>
public sealed class S_CLOSEUI1_D2_SourceContractTests
{
    [Fact]
    public void Resolve_Source_UsesEqualsNormalized_NoLegacyOrdinalIgnoreCaseNoNameFallback()
    {
        var source = File.ReadAllText(Path.Combine(CoreSourceRoot(), "CloseApps", "CloseAppsService.cs"));

        Assert.Contains("ExecutablePathKey.EqualsNormalized(process.ExecutablePath, path)", source);
        // 旧实现（原始路径 OrdinalIgnoreCase 直比）已移除；Resolve 不做任何名称兜底。
        Assert.DoesNotContain("string.Equals(process.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)", source);
        Assert.Contains("// S-CLOSEUI1-D2", source);
    }

    [Fact]
    public void ServiceRegistration_Order_StillOfficeSaveRunCommandsCloseApps()
    {
        var registration = File.ReadAllText(Path.Combine(AppSourceRoot(), "AppHost", "ServiceRegistration.cs"));

        // 固定顺序注释仍在（既有契约断言）。
        Assert.Contains("OfficeSave→RunCommands→CloseApps", registration);
        // Pre-Pipeline 动作链实际注册顺序不变：以唯一构造器 token 定位，避免被 using 语句干扰。
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

        // 唯一 IPowerService 生产出口仍是 ShutdownWorkflow。
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
