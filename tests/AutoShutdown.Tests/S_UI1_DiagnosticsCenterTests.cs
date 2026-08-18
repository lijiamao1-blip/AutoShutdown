using System.IO;
using System.IO.Compression;
using System.Text;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Diagnostics;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-UI1 聚焦测试：导航真实化、诊断中心 VM（日志/筛选/自检/复制）、
/// 脱敏导出（密钥类始终排除 + 隐私开关）、安全自检（只读）、配置恢复的 S-UI1 视角。
/// </summary>
public sealed class S_UI1_DiagnosticsCenterTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    // ==================== 1. 导航：7 个页面，同一时刻仅一个可见 ====================

    [Fact]
    public void Navigation_SevenPages_ExactlyOneVisibleAtATime()
    {
        var viewModel = CreateMainViewModel(CreateRunningEngine());

        Assert.Equal(7, viewModel.NavItems.Count);
        var keys = new[] { "home", "tasks", "advanced", "wol", "logs", "settings", "about" };

        foreach (var key in keys)
        {
            var nav = viewModel.NavItems.Single(item => item.PageKey == key);
            Assert.Equal(key, nav.PageKey);

            viewModel.SelectedNav = nav;
            foreach (var other in keys)
            {
                Assert.Equal(other == key, IsPageVisible(viewModel, other));
            }
        }
    }

    [Fact]
    public void Navigation_AllNavItems_HaveRealTitles()
    {
        var viewModel = CreateMainViewModel(CreateRunningEngine());
        var titles = viewModel.NavItems.Select(item => item.Title).ToArray();
        Assert.Equal(
            new[] { "首页", "任务管理", "高级功能", "网络唤醒", "日志与诊断", "软件设置", "关于软件" },
            titles);
    }

    // ==================== 2. BuildDiagnosticsLiveSummary：只读快照映射 ====================

    [Fact]
    public void BuildDiagnosticsLiveSummary_NoSections_ReturnsEmptySafeSummary()
    {
        var viewModel = CreateMainViewModel(CreateRunningEngine());
        var summary = viewModel.BuildDiagnosticsLiveSummary();

        Assert.Empty(summary.Tasks);
        Assert.Empty(summary.WolTargets);
        Assert.Empty(summary.RemoteDevices);
        Assert.False(summary.RemoteEnabled);
        Assert.Equal("无", summary.RemotePinPresenceText);
        // 未启用同步不算不健康（默认不开启）
        Assert.True(summary.TaskSyncHealthy);
        Assert.False(string.IsNullOrEmpty(summary.ConfigStatusText));
        Assert.Equal(viewModel.VersionText, summary.VersionText);
    }

    [Fact]
    public void BuildDiagnosticsLiveSummary_WithTask_MapsOneTaskRow()
    {
        var instance = WaitingInstance();
        var engine = CreateRunningEngine(instance);
        var viewModel = CreateMainViewModel(engine);
        viewModel.Refresh(engine.Snapshot, Now);

        var summary = viewModel.BuildDiagnosticsLiveSummary();

        var row = Assert.Single(summary.Tasks);
        Assert.Equal("关机", row.ActionText);
        Assert.NotEmpty(row.FireTimeText);
    }

    [Fact]
    public async Task BuildDiagnosticsLiveSummary_ConfigRecovered_ConfigUsableTrue_NoRealPower()
    {
        // 配置缺失 → 初始化入口出现；执行初始化后 TestMode=true（CreateSafeDefaultAsync 固定安全模式），
        // 快照报告 ConfigUsable=true，且从不自动开启真实电源（RealPowerEnabled 维持 false）。
        var initService = new FakeConfigurationService(MissingResult());
        var viewModel = CreateMainViewModel(CreateRunningEngine(), initService);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsConfigInitVisible);
        Assert.False(viewModel.BuildDiagnosticsLiveSummary().ConfigUsable);

        await viewModel.InitializeConfigCommand.ExecuteAsync();

        Assert.True(initService.CreateSafeDefaultCalled);
        Assert.False(viewModel.IsConfigInitVisible);
        var summary = viewModel.BuildDiagnosticsLiveSummary();
        Assert.True(summary.ConfigUsable);
        Assert.True(summary.ConfigStatusText.Contains("安全", StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 3. DiagnosticsRedactor：密钥始终脱敏 + 隐私开关 ====================

    [Fact]
    public void RedactFile_SecretProperties_AlwaysMaskedEvenWithPrivacyOn()
    {
        const string json =
            """{"name":"srv","pin":"123456","hmacSharedSecret":"abc-def","pfxPassword":"p@ss","hostPath":"C:\\Users\\bob"}""";

        foreach (var includePrivacy in new[] { false, true })
        {
            var redacted = DiagnosticsRedactor.RedactFile(json, includePrivacy);
            Assert.DoesNotContain("123456", redacted);
            Assert.DoesNotContain("abc-def", redacted);
            Assert.DoesNotContain("p@ss", redacted);
            Assert.Contains(DiagnosticsRedactor.RedactedMarker, redacted);
        }
    }

    [Fact]
    public void RedactFile_PrivacyOff_MasksPathIpMac()
    {
        const string json =
            """{"mac":"AA:BB:CC:DD:EE:FF","ip":"192.168.1.50","path":"C:\\Users\\bob\\config.json"}""";

        var redacted = DiagnosticsRedactor.RedactFile(json, includePrivacy: false);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", redacted);
        Assert.DoesNotContain("192.168.1.50", redacted);
        Assert.DoesNotContain("bob", redacted);
        Assert.Contains("**:**:**:**:**:**", redacted);
        Assert.Contains("[IP已脱敏]", redacted);
    }

    [Fact]
    public void RedactFile_PrivacyOn_PreservesPrivacyKeepsSecretsMasked()
    {
        const string json =
            """{"mac":"AA:BB:CC:DD:EE:FF","ip":"192.168.1.50","path":"C:\\Users\\bob\\config.json","pin":"7777"}""";

        var redacted = DiagnosticsRedactor.RedactFile(json, includePrivacy: true);
        Assert.Contains("AA:BB:CC:DD:EE:FF", redacted);
        Assert.Contains("192.168.1.50", redacted);
        Assert.Contains("bob", redacted);
        Assert.DoesNotContain("7777", redacted);
    }

    [Fact]
    public void Redact_LogLine_MasksPathAndIp()
    {
        const string line =
            "2024-01-15T11:00:00.000+08:00 [Information] Config 读取配置失败：192.168.1.5 C:\\Users\\bob\\config.json";

        var redacted = DiagnosticsRedactor.Redact(line, includePrivacy: false);
        Assert.DoesNotContain("192.168.1.5", redacted);
        Assert.DoesNotContain("bob", redacted);
        Assert.Contains("[IP已脱敏]", redacted);
    }

    // ==================== 4. DiagnosticsPackageExporter：脱敏导出 ====================

    private static DiagnosticsExportContent SampleExportContent(string dataRoot, string logDirectory, bool includePrivacy)
        => new(
            AppName: "电脑自动关机助手",
            VersionText: "v2.0.0-PKG.fe54711",
            BuildCommitText: "fe54711",
            SigningStatusText: "unsigned-candidate",
            HeaderModeText: "安全测试模式",
            ConfigStatusText: "安全有效",
            SchedulerStatusText: "运行中",
            DataRoot: dataRoot,
            LogDirectory: logDirectory,
            ConfigJsonText:
                """{"SchemaVersion":1,"pin":"9876","hmacSharedSecret":"xyz","path":"C:\\Users\\bob\\config.json"}""",
            Tasks:
            [
                new DiagnosticsTaskRow("每天固定时间", "关机", "12:00:00", "等待中", "倒计时 01:00:00")
            ],
            WolTargets:
            [
                new DiagnosticsWolTargetRow("卧室电脑", "AA:BB:CC:DD:EE:FF", "192.168.1.255", "9")
            ],
            Remote: new DiagnosticsRemoteStatus(
                Enabled: false,
                ListenAddress: "127.0.0.1",
                ListenPortText: "48620",
                RequireTls: true,
                PinPresenceText: "已生成（值不导出）",
                Devices:
                [
                    new DiagnosticsPairedDevice("手机", "device-1", "2024-01-15 10:00")
                ]),
            SelfCheckItems: [],
            IncludePrivacyInfo: includePrivacy,
            CreatedAtUtc: new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Export_PrivacyOff_MasksAll_ExcludesSecretFiles()
    {
        using var temp = TempDir.Create();
        var logDirectory = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, "autoshutdown-2024-01-15.log"),
            "2024-01-15T11:00:00.000+08:00 [Information] Event 路径 C:\\Users\\bob；IP 192.168.1.5\n");

        var zipPath = Path.Combine(temp.Path, "out.zip");
        var exporter = new DiagnosticsPackageExporter(new NullLogger(logDirectory));
        var ok = await exporter.ExportAsync(
            SampleExportContent(temp.Path, logDirectory, includePrivacy: false),
            zipPath,
            CancellationToken.None);
        Assert.True(ok);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("README.txt", entries);
        Assert.Contains("summary.txt", entries);
        Assert.Contains("config-redacted.json", entries);
        Assert.Contains("wol-targets.txt", entries);
        Assert.Contains("remote-status.txt", entries);
        Assert.Contains("logs/autoshutdown-2024-01-15.log", entries);
        // 持密钥文件绝不进包
        Assert.DoesNotContain(entries, name => name.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase));

        var config = ReadZipEntry(archive, "config-redacted.json");
        Assert.DoesNotContain("9876", config);
        Assert.DoesNotContain("xyz", config);
        Assert.DoesNotContain("bob", config);

        var wol = ReadZipEntry(archive, "wol-targets.txt");
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", wol);
        Assert.DoesNotContain("192.168.1.255", wol);

        var remote = ReadZipEntry(archive, "remote-status.txt");
        Assert.DoesNotContain("device-1", remote);
        // PIN 只以「存在性」描述出现，绝不带出真实值
        Assert.Contains("当前 PIN：已生成（值不导出）", remote);
        Assert.DoesNotContain("123456", remote);

        var log = ReadZipEntry(archive, "logs/autoshutdown-2024-01-15.log");
        Assert.DoesNotContain("bob", log);
        Assert.DoesNotContain("192.168.1.5", log);
    }

    [Fact]
    public async Task Export_PrivacyOn_PreservesPrivacy_SecretsStillMasked()
    {
        using var temp = TempDir.Create();
        var zipPath = Path.Combine(temp.Path, "out2.zip");
        var exporter = new DiagnosticsPackageExporter(new NullLogger(Path.Combine(temp.Path, "logs")));

        var ok = await exporter.ExportAsync(
            SampleExportContent(temp.Path, Path.Combine(temp.Path, "logs"), includePrivacy: true),
            zipPath,
            CancellationToken.None);
        Assert.True(ok);

        using var archive = ZipFile.OpenRead(zipPath);
        var config = ReadZipEntry(archive, "config-redacted.json");
        Assert.Contains("bob", config);
        Assert.DoesNotContain("9876", config);
        Assert.DoesNotContain("xyz", config);

        var wol = ReadZipEntry(archive, "wol-targets.txt");
        Assert.Contains("AA:BB:CC:DD:EE:FF", wol);
    }

    [Fact]
    public void BuildSummaryText_ReportsPrivacyAndSecretsPolicy()
    {
        var exporter = new DiagnosticsPackageExporter(new NullLogger("C:\\logs"));
        var summary = exporter.BuildSummaryText(
            SampleExportContent("C:\\data", "C:\\logs", includePrivacy: false));

        Assert.Contains("包含隐私信息：否（已脱敏）", summary);
        Assert.Contains("远程控制：未启用", summary);
        Assert.Contains("WoL 目标数：1", summary);
        Assert.Contains("unsigned-candidate", summary);
    }

    // ==================== 5. SafetySelfCheckRunner：只读自检 ====================

    [Fact]
    public void SelfCheck_AllHealthy_ReportsSixItemsAllPass()
    {
        using var temp = TempDir.Create();
        var snapshot = new SelfCheckSnapshot(
            DataRoot: temp.Path,
            LogDirectory: Path.Combine(temp.Path, "logs"),
            ConfigStatusText: "安全有效",
            ConfigUsable: true,
            LocalTaskCount: 1,
            TaskSyncStatusText: "已启用",
            TaskSyncHealthy: true,
            TaskSyncDetailText: "上次同步 10:00",
            WolTargets: [new WolTargetHealth("卧室电脑", true, "合法")],
            RemoteEnabled: false,
            RemoteListenAddress: "127.0.0.1",
            RemoteListenPort: 48620,
            RemoteRequireTls: true,
            RemoteDetailText: string.Empty);

        var report = new SafetySelfCheckRunner().Run(snapshot);

        Assert.True(report.AllHealthy);
        Assert.Equal(6, report.Items.Count);
        Assert.All(report.Items, item => Assert.True(item.IsHealthy));
    }

    [Fact]
    public void SelfCheck_DataRootNotWritable_ReportsUnhealthy_NoMutation()
    {
        using var temp = TempDir.Create();
        var filePath = Path.Combine(temp.Path, "occupied.txt");
        File.WriteAllText(filePath, "x");
        var originalContent = File.ReadAllText(filePath);

        var snapshot = new SelfCheckSnapshot(
            DataRoot: filePath,
            LogDirectory: Path.Combine(temp.Path, "logs"),
            ConfigStatusText: "安全有效",
            ConfigUsable: true,
            LocalTaskCount: 0,
            TaskSyncStatusText: "未启用",
            TaskSyncHealthy: true,
            TaskSyncDetailText: string.Empty,
            WolTargets: [],
            RemoteEnabled: false,
            RemoteListenAddress: "127.0.0.1",
            RemoteListenPort: null,
            RemoteRequireTls: true,
            RemoteDetailText: string.Empty);

        var report = new SafetySelfCheckRunner().Run(snapshot);

        var dataRootItem = report.Items.Single(item => item.Key == "dataRootWritable");
        Assert.False(dataRootItem.IsHealthy);
        // 探针失败绝不改动既有数据
        Assert.Equal(originalContent, File.ReadAllText(filePath));
    }

    [Fact]
    public void SelfCheck_UnhealthyConfigWolAndRemote_Detected()
    {
        using var temp = TempDir.Create();
        var snapshot = new SelfCheckSnapshot(
            DataRoot: temp.Path,
            LogDirectory: Path.Combine(temp.Path, "logs"),
            ConfigStatusText: "配置损坏",
            ConfigUsable: false,
            LocalTaskCount: 0,
            TaskSyncStatusText: "同步失败",
            TaskSyncHealthy: false,
            TaskSyncDetailText: "计划任务创建被拒",
            WolTargets: [new WolTargetHealth("客厅机", false, "MAC 格式非法")],
            RemoteEnabled: true,
            RemoteListenAddress: "0.0.0.0",
            RemoteListenPort: 48620,
            RemoteRequireTls: false,
            RemoteDetailText: string.Empty);

        var report = new SafetySelfCheckRunner().Run(snapshot);

        Assert.False(report.AllHealthy);
        Assert.False(report.Items.Single(item => item.Key == "config").IsHealthy);
        Assert.False(report.Items.Single(item => item.Key == "taskSync").IsHealthy);
        Assert.False(report.Items.Single(item => item.Key == "wolTargets").IsHealthy);
        Assert.False(report.Items.Single(item => item.Key == "remoteListen").IsHealthy);
        Assert.False(report.SummaryText.Contains("全部通过", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelfCheck_RunSelfCheckAsync_MatchesSyncRun()
    {
        using var temp = TempDir.Create();
        var snapshot = new SelfCheckSnapshot(
            DataRoot: temp.Path,
            LogDirectory: temp.Path,
            ConfigStatusText: "安全有效",
            ConfigUsable: true,
            LocalTaskCount: 0,
            TaskSyncStatusText: "未启用",
            TaskSyncHealthy: true,
            TaskSyncDetailText: string.Empty,
            WolTargets: [],
            RemoteEnabled: false,
            RemoteListenAddress: "127.0.0.1",
            RemoteListenPort: null,
            RemoteRequireTls: true,
            RemoteDetailText: string.Empty);

        var runner = new SafetySelfCheckRunner();
        var sync = runner.Run(snapshot);
        var asyncReport = await runner.RunSelfCheckAsync(snapshot, CancellationToken.None);

        Assert.Equal(sync.AllHealthy, asyncReport.AllHealthy);
        Assert.Equal(sync.Items.Count, asyncReport.Items.Count);
    }

    // ==================== 6. DiagnosticsCenterViewModel：日志查看/筛选/复制/自检 ====================

    private const string LogLineError =
        "2024-01-15T11:00:00.000+08:00 [Error] Startup 初始化失败：config.json 损坏";
    private const string LogLineWarning =
        "2024-01-15T11:00:00.000+08:00 [Warning] Sync 计划任务同步失败";
    private const string LogLineInfo =
        "2024-01-15T11:00:00.000+08:00 [Information] Task 任务已创建";

    [Fact]
    public void DiagnosticsVm_RefreshLogFiles_LoadsAndFiltersEntries()
    {
        using var temp = TempDir.Create();
        var logDirectory = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, "autoshutdown-2024-01-15.log"),
            string.Join("\n", LogLineError, LogLineWarning, LogLineInfo));

        var vm = CreateDiagnosticsViewModel(temp.Path, logDirectory);
        vm.RefreshLogFiles();

        Assert.Single(vm.LogFileNames);
        Assert.Equal("autoshutdown-2024-01-15.log", vm.SelectedLogFileName);
        Assert.Equal(3, vm.LogEntries.Count);

        // 级别筛选：错误（Error+Warning）
        vm.LevelFilterIndex = 1;
        Assert.Equal(2, vm.LogEntries.Count);
        Assert.All(vm.LogEntries, entry => Assert.True(entry.Level is "Error" or "Warning"));

        // 警告级别：仅 Warning
        vm.LevelFilterIndex = 2;
        var warningOnly = Assert.Single(vm.LogEntries);
        Assert.Equal("Warning", warningOnly.Level);

        // 仅错误/警告开关与级别筛选叠加
        vm.ShowErrorsOnly = true;
        Assert.Single(vm.LogEntries);

        // 搜索恢复
        vm.ShowErrorsOnly = false;
        vm.LevelFilterIndex = 0;
        vm.SearchText = "已创建";
        var searched = Assert.Single(vm.LogEntries);
        Assert.Contains("已创建", searched.Message);
    }

    [Fact]
    public void DiagnosticsVm_CopySelectedLog_WritesFullText()
    {
        using var temp = TempDir.Create();
        var logDirectory = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, "autoshutdown-2024-01-15.log"),
            LogLineError);

        var clipboard = new List<string>();
        var vm = CreateDiagnosticsViewModel(temp.Path, logDirectory, clipboardWriter: clipboard.Add);
        vm.RefreshLogFiles();

        vm.SelectedLogEntry = vm.LogEntries[0];
        vm.CopySelectedLogCommand.Execute(null);

        Assert.Single(clipboard);
        Assert.Equal(LogLineError, clipboard[0]);
    }

    [Fact]
    public async Task DiagnosticsVm_RunSelfCheckCommand_PopulatesItems()
    {
        using var temp = TempDir.Create();
        var logDirectory = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logDirectory);

        var summary = new DiagnosticsLiveSummary(
            VersionText: "v2.0.0-PKG.fe54711",
            BuildCommitText: "fe54711",
            SigningStatusText: "unsigned-candidate",
            HeaderModeText: "安全测试模式",
            ConfigStatusText: "安全有效",
            ConfigUsable: true,
            SchedulerStatusText: "运行中",
            DataRoot: temp.Path,
            LogDirectory: logDirectory,
            Tasks: [],
            WolTargets: [new DiagnosticsWolTargetRow("卧室电脑", "AA:BB:CC:DD:EE:FF", string.Empty, string.Empty)],
            TaskSyncHealthy: true,
            TaskSyncStatusText: "未启用",
            TaskSyncDetailText: string.Empty,
            RemoteEnabled: false,
            RemoteListenAddress: "127.0.0.1",
            RemoteListenPort: null,
            RemoteRequireTls: true,
            RemotePinPresenceText: "无",
            RemoteDevices: []);

        var vm = CreateDiagnosticsViewModel(temp.Path, logDirectory, summary: summary);
        await vm.RunSelfCheckCommand.ExecuteAsync();

        Assert.Equal(6, vm.SelfCheckItems.Count);
        Assert.True(vm.SelfCheckItems.Single(item => item.Key == "config").IsHealthy);
        Assert.False(string.IsNullOrEmpty(vm.SelfCheckSummaryText));
        Assert.False(vm.HasStatusError);
    }

    [Fact]
    public async Task DiagnosticsVm_SelfCheckFailure_ReportsStatusError_NoAutoFix()
    {
        using var temp = TempDir.Create();
        var logDirectory = Path.Combine(temp.Path, "logs");

        var summary = new DiagnosticsLiveSummary(
            VersionText: "v",
            BuildCommitText: string.Empty,
            SigningStatusText: "unsigned-candidate",
            HeaderModeText: "安全测试模式",
            ConfigStatusText: "配置损坏",
            ConfigUsable: false,
            SchedulerStatusText: "运行中",
            DataRoot: temp.Path,
            LogDirectory: logDirectory,
            Tasks: [],
            WolTargets: [],
            TaskSyncHealthy: false,
            TaskSyncStatusText: "同步失败",
            TaskSyncDetailText: "计划任务创建被拒",
            RemoteEnabled: true,
            RemoteListenAddress: "0.0.0.0",
            RemoteListenPort: 48620,
            RemoteRequireTls: false,
            RemotePinPresenceText: "锁定中",
            RemoteDevices: []);

        var vm = CreateDiagnosticsViewModel(temp.Path, logDirectory, summary: summary);
        await vm.RunSelfCheckCommand.ExecuteAsync();

        // 不健康项被只读报告（未自动修复），且不伪装成通过；这是"待关注"而非执行"失败"
        Assert.False(vm.HasStatusError);
        Assert.False(vm.SelfCheckItems.Single(item => item.Key == "config").IsHealthy);
        Assert.False(vm.SelfCheckItems.Single(item => item.Key == "remoteListen").IsHealthy);
        Assert.Contains("待关注", vm.SelfCheckSummaryText);
        Assert.DoesNotContain("全部通过", vm.SelfCheckSummaryText);
        Assert.Contains("未自动修复", vm.SelfCheckSummaryText);
    }

    // ==================== 辅助 ====================

    private static bool IsPageVisible(MainWindowViewModel viewModel, string pageKey)
        => pageKey switch
        {
            "home" => viewModel.IsHomeVisible,
            "tasks" => viewModel.IsTasksPageVisible,
            "advanced" => viewModel.IsAdvancedPageVisible,
            "wol" => viewModel.IsWolPageVisible,
            "logs" => viewModel.IsLogsPageVisible,
            "settings" => viewModel.IsSettingsPageVisible,
            "about" => viewModel.IsAboutPageVisible,
            _ => throw new ArgumentOutOfRangeException(nameof(pageKey))
        };

    private static MainWindowViewModel CreateMainViewModel(
        FakeSchedulerEngine engine,
        FakeConfigurationService? configurationService = null)
        => new(
            engine,
            configurationService ?? new FakeConfigurationService(SuccessResult()),
            new FakeClock(Now),
            new NullLogger(Path.Combine(Path.GetTempPath(), "ui1-test-logs")),
            new FakeAutoStartService());

    private static DiagnosticsCenterViewModel CreateDiagnosticsViewModel(
        string dataRoot,
        string logDirectory,
        Action<string>? clipboardWriter = null,
        DiagnosticsLiveSummary? summary = null)
    {
        var logger = new NullLogger(logDirectory);
        var summaryProvider = summary is null
            ? new Func<DiagnosticsLiveSummary>(() => new DiagnosticsLiveSummary(
                VersionText: "v",
                BuildCommitText: string.Empty,
                SigningStatusText: "unsigned-candidate",
                HeaderModeText: "安全测试模式",
                ConfigStatusText: "安全有效",
                ConfigUsable: true,
                SchedulerStatusText: "运行中",
                DataRoot: dataRoot,
                LogDirectory: logDirectory,
                Tasks: [],
                WolTargets: [],
                TaskSyncHealthy: true,
                TaskSyncStatusText: "未启用",
                TaskSyncDetailText: string.Empty,
                RemoteEnabled: false,
                RemoteListenAddress: "127.0.0.1",
                RemoteListenPort: null,
                RemoteRequireTls: true,
                RemotePinPresenceText: "无",
                RemoteDevices: []))
            : new Func<DiagnosticsLiveSummary>(() => summary);

        return new DiagnosticsCenterViewModel(
            logger,
            new FakeShellOpen(),
            new SafetySelfCheckRunner(),
            new DiagnosticsPackageExporter(logger),
            new FakeScreenshotService(),
            new FakeClock(Now),
            () => null,
            () => dataRoot,
            summaryProvider,
            clipboardWriter);
    }

    private static string ReadZipEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static ConfigurationLoadResult MissingResult()
        => new() { Status = ConfigurationLoadStatus.Missing, Errors = ["missing"] };

    private static ConfigurationLoadResult SuccessResult()
        => new()
        {
            Status = ConfigurationLoadStatus.Success,
            Config = new AppConfig
            {
                SchemaVersion = 1,
                TestMode = true,
                AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
            }
        };

    private static ConfigurationSaveResult SuccessSaveResult()
        => new() { Status = ConfigurationSaveStatus.Success };

    private static FakeSchedulerEngine CreateRunningEngine(TaskInstance? instance = null)
        => new()
        {
            Snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                Instances = instance is null
                    ? new Dictionary<Guid, TaskInstance>()
                    : new Dictionary<Guid, TaskInstance> { [instance.SourceTaskId] = instance },
                LastUpdatedAt = Now
            }
        };

    private static TaskInstance WaitingInstance() => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Waiting,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        HasExecuted = false,
        CreatedAt = Now
    };

    private sealed class TempDir : IDisposable
    {
        private TempDir(string path) => Path = path;

        public string Path { get; }

        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "autoshutdown-ui1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDir(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // 清理失败不影响测试结果。
            }
        }
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
            => Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });

        public SchedulerSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class FakeConfigurationService : IConfigurationService
    {
        private ConfigurationLoadResult _result;

        public FakeConfigurationService(ConfigurationLoadResult result) => _result = result;

        public bool CreateSafeDefaultCalled { get; private set; }

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => Task.FromResult(SuccessSaveResult());

        /// <summary>模拟「初始化安全配置」：成功后磁盘变为安全测试模式，后续 LoadAsync 反映该状态。</summary>
        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
        {
            CreateSafeDefaultCalled = true;
            _result = SuccessResult();
            return Task.FromResult(SuccessSaveResult());
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public AutoStartStatus Status { get; set; } = AutoStartStatus.Disabled;

        public AutoStartStatus GetStatus() => Status;

        public AutoStartOperationResult Enable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class NullLogger : IApplicationLogger
    {
        public NullLogger(string logDirectory) => LogDirectory = logDirectory;

        public string LogDirectory { get; }

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeShellOpen : IShellOpenService
    {
        public List<string> OpenedDirectories { get; } = new();

        public void OpenDirectory(string path) => OpenedDirectories.Add(path);

        public void OpenFolderAndSelectFile(string filePath)
        {
        }
    }

    private sealed class FakeScreenshotService : IWindowScreenshotService
    {
        public bool SaveCurrentWindowScreenshot(out string? savedPath)
        {
            savedPath = null;
            return false;
        }
    }
}
