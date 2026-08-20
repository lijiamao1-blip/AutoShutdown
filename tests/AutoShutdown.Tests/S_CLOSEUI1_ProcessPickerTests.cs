using System.Diagnostics;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.ProcessSelection;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-CLOSEUI1 聚焦测试。覆盖「从运行中的进程选择关闭目标」的只读安全边界：
/// 1) 安全资格评估器（ProcessSelectionGuard）纯函数规则：自身/路径不可读/相对路径/环境变量
///    占位/不存在/reparse junction/会话不符/辅助进程/系统关键进程/已添加 一律不可选，
///    普通文件且同会话才可选；单条失败绝不抛给整表。
/// 2) 进程选择 VM：枚举失败整表 fail-closed、搜索、全部取消、确定时再次复核（已退出/路径变化
///    被排除）、取消不改动。
/// 3) MainWindowViewModel 接入：打开/合并/按规范化路径去重/绝不持久化 PID/绝不授予强杀/
///    保存与重载一致/失效路径提示与重新选择替换。
/// 全程使用替身进程信息提供器与替身窗口启动器，绝不枚举/启动/关闭真实进程，不触碰电源。
/// </summary>
public sealed class S_CLOSEUI1_ProcessPickerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2024, 2, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 清理失败不影响测试结论。
            }
        }
    }

    // ==================== 安全资格评估器（纯函数） ====================

    [Fact]
    public void Guard_SelfPid_NotSelectable()
    {
        var context = new ProcessSelectionContext { CurrentProcessId = 4242, CurrentSessionId = 1 };
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", CreateTempFile(), pid: 4242, sessionId: 1),
            context);

        Assert.False(decision.Selectable);
        Assert.Contains("自身", decision.Reason);
    }

    [Fact]
    public void Guard_EmptyPath_NotSelectable()
    {
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", null, pid: 10, sessionId: 1),
            Context());

        Assert.False(decision.Selectable);
        Assert.Contains("路径", decision.Reason);
    }

    [Fact]
    public void Guard_RelativePath_NotSelectable()
    {
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", "app.exe", pid: 10, sessionId: 1),
            Context());

        Assert.False(decision.Selectable);
        Assert.Contains("绝对路径", decision.Reason);
    }

    [Fact]
    public void Guard_EnvPlaceholderPath_NotSelectable()
    {
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", @"C:\Windows\%WINDIR%\app.exe", pid: 10, sessionId: 1),
            Context());

        Assert.False(decision.Selectable);
        Assert.Contains("环境变量", decision.Reason);
    }

    [Fact]
    public void Guard_NonExistentPath_NotSelectable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "as-closeui1-missing-" + Guid.NewGuid().ToString("N") + ".exe");
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", missing, pid: 10, sessionId: 1),
            Context());

        Assert.False(decision.Selectable);
    }

    [Fact]
    public void Guard_DirectoryPath_NotSelectable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "as-closeui1-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var decision = ProcessSelectionGuard.Evaluate(
                RunningProcess("app.exe", directory, pid: 10, sessionId: 1),
                Context());

            Assert.False(decision.Selectable);
        }
        finally
        {
            try { Directory.Delete(directory); } catch { }
        }
    }

    [Fact]
    public void Guard_JunctionPath_NotSelectable_FailClosed()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "as-closeui1-junction-" + Guid.NewGuid().ToString("N"));
        var realDir = Path.Combine(baseDir, "real");
        var link = Path.Combine(baseDir, "link");
        Directory.CreateDirectory(realDir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(realDir);
            using var process = Process.Start(psi)!;
            process.WaitForExit(5000);
            Assert.True(process.ExitCode == 0, "mklink /J should succeed without elevation on this machine");

            var decision = ProcessSelectionGuard.Evaluate(
                RunningProcess("app.exe", link, pid: 10, sessionId: 1),
                Context());

            // junction/reparse 路径 fail-closed：绝不可选（无论命中哪个拒绝分支）。
            Assert.False(decision.Selectable);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Guard_SessionMismatch_NotSelectable()
    {
        var path = CreateTempFile();
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", path, pid: 10, sessionId: 3), // 系统服务/其他会话
            Context(sessionId: 1));

        Assert.False(decision.Selectable);
        Assert.Contains("会话", decision.Reason);
    }

    [Fact]
    public void Guard_UnknownSession_NotSelectable()
    {
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", CreateTempFile(), pid: 10, sessionId: -1),
            Context(sessionId: 1));

        Assert.False(decision.Selectable);
    }

    [Fact]
    public void Guard_SelfExecutablePath_NotSelectable()
    {
        var selfPath = CreateTempFile();
        var context = new ProcessSelectionContext { CurrentProcessId = 999, CurrentSessionId = 1, CurrentExecutablePath = selfPath };
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", selfPath, pid: 10, sessionId: 1),
            context);

        Assert.False(decision.Selectable);
        Assert.Contains("自身", decision.Reason);
    }

    [Fact]
    public void Guard_HelperExecutableName_NotSelectable()
    {
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("AutoShutdown.OfficeSaveHelper", CreateTempFile(), pid: 10, sessionId: 1),
            Context(sessionId: 1));

        Assert.False(decision.Selectable);
        Assert.Contains("辅助进程", decision.Reason);
    }

    [Fact]
    public void Guard_CriticalSystemName_NotSelectable()
    {
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("svchost", CreateTempFile(), pid: 10, sessionId: 1),
            Context(sessionId: 1));

        Assert.False(decision.Selectable);
        Assert.Contains("系统关键进程", decision.Reason);
    }

    [Fact]
    public void Guard_AlreadyAdded_NotSelectable()
    {
        var path = CreateTempFile();
        var context = new ProcessSelectionContext
        {
            CurrentSessionId = 1,
            ExistingTargetPaths = [path]
        };
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", path, pid: 10, sessionId: 1),
            context);

        Assert.False(decision.Selectable);
        Assert.Equal("已添加", decision.Reason);
    }

    [Fact]
    public void Guard_OrdinaryFile_SameSession_Selectable()
    {
        var path = CreateTempFile();
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("myapp.exe", path, pid: 10, sessionId: 1),
            Context(sessionId: 1));

        Assert.True(decision.Selectable);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Guard_PerProcessRejection_IsIsolated_NeverThrows()
    {
        // 一条进程路径不可读，绝不影响其他进程的可选性（按行标灰而非整表失败）。
        var context = Context(sessionId: 1);
        var bad = RunningProcess("bad.exe", null, pid: 1, sessionId: 1);
        var good = RunningProcess("good.exe", CreateTempFile(), pid: 2, sessionId: 1);

        var badDecision = ProcessSelectionGuard.Evaluate(bad, context);
        var goodDecision = ProcessSelectionGuard.Evaluate(good, context);

        Assert.False(badDecision.Selectable);
        Assert.True(goodDecision.Selectable);
    }

    // ==================== 进程选择 VM ====================

    [Fact]
    public async Task Picker_BuildAndSearch_FiltersRows()
    {
        var provider = new FakeProcessInfoProvider(
            [
                RunningProcess("notepad", NotepadPath(), pid: 101, sessionId: 1, windowTitle: "文档 - 记事本"),
                RunningProcess("calc", CreateTempFile(), pid: 102, sessionId: 1, productName: "计算器")
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));

        await picker.RefreshAsync();

        Assert.Equal(2, picker.Rows.Count);
        Assert.Equal("共 2 个进程，2 个可添加", picker.StatusText);

        picker.SearchText = "note";
        var row = Assert.Single(picker.Rows);
        Assert.Equal(101, row.Info.ProcessId);

        picker.SearchText = "";
        Assert.Equal(2, picker.Rows.Count);
    }

    [Fact]
    public async Task Picker_EnumerateFails_EmptyRowsFailClosed()
    {
        var provider = new FakeProcessInfoProvider([], currentSessionId: 1)
        {
            ThrowOnEnumerate = true
        };
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));

        await picker.RefreshAsync();

        Assert.True(picker.HasError);
        Assert.Empty(picker.Rows);
        Assert.Contains("失败", picker.StatusText);
    }

    [Fact]
    public void Picker_ClearAll_UnchecksOnlySelectable()
    {
        var path = CreateTempFile();
        var provider = new FakeProcessInfoProvider(
            [
                RunningProcess("good.exe", path, pid: 101, sessionId: 1),
                RunningProcess("svchost", path, pid: 102, sessionId: 1) // 不可选（系统关键进程）
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));

        // 同步构建行（替身枚举，不触碰真实进程）。
        RefreshSynchronously(picker);
        picker.Rows[0].IsSelected = true;
        Assert.True(picker.Rows[0].IsSelected);

        picker.ClearAll();

        Assert.False(picker.Rows[0].IsSelected);
    }

    [Fact]
    public async Task Picker_Confirm_RechecksProcessStillExists()
    {
        var path = CreateTempFile();
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("good.exe", path, pid: 101, sessionId: 1)],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var result = picker.ConfirmSelection();

        Assert.Single(result!.ConfirmedProcesses);
        Assert.Equal(101, result.ConfirmedProcesses[0].ProcessId);
        Assert.Equal(1, provider.GetByIdCalls);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Picker_Confirm_ExitedProcess_ExcludedWithWarning()
    {
        var path = CreateTempFile();
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("good.exe", path, pid: 101, sessionId: 1)],
            currentSessionId: 1)
        {
            GetByIdReturnsNull = true
        };
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var result = picker.ConfirmSelection();

        Assert.NotNull(result);
        Assert.Empty(result.ConfirmedProcesses);
        Assert.True(picker.HasWarnings);
        Assert.Contains("已退出", result.Warnings);
    }

    [Fact]
    public async Task Picker_Confirm_PathChanged_ExcludedWithWarning()
    {
        var original = CreateTempFile();
        var changed = CreateTempFile();
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("good.exe", original, pid: 101, sessionId: 1)],
            currentSessionId: 1)
        {
            GetByIdPathOverride = changed // 复核时路径已变化。
        };
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var result = picker.ConfirmSelection();

        Assert.NotNull(result);
        Assert.Empty(result.ConfirmedProcesses);
        Assert.Contains("路径已变化", result.Warnings);
    }

    [Fact]
    public async Task Picker_Confirm_NothingChecked_ReturnsEmptyNoWarnings()
    {
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("good.exe", CreateTempFile(), pid: 101, sessionId: 1)],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        var result = picker.ConfirmSelection();

        Assert.NotNull(result);
        Assert.Empty(result.ConfirmedProcesses);
        Assert.False(picker.HasWarnings);
    }

    // ==================== MainWindowViewModel 接入 ====================

    [Fact]
    public async Task OpenPicker_AddsConfirmed_DedupByNormalizedPath_NoPid_NoForceKill()
    {
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();
        var provider = new FakeProcessInfoProvider(
            [
                RunningProcess("appA.exe", pathA, pid: 1, sessionId: 1, productName: "甲产品", companyName: "甲公司"),
                RunningProcess("appA.exe", pathA, pid: 2, sessionId: 1, windowTitle: "第二个实例"),
                RunningProcess("appB.exe", pathB, pid: 3, sessionId: 1, productName: "乙产品")
            ],
            currentSessionId: 1);
        ProcessPickerViewModel? captured = null;

        var viewModel = CreateViewModel(
            ConfigWithoutCloseApps(),
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                captured = pickerVm;
                RefreshSynchronously(pickerVm); // 生产环境由窗口 Loaded 触发，替身启动器需手动刷新。
                foreach (var row in pickerVm.Rows.Where(r => r.Selectable))
                {
                    row.IsSelected = true;
                }

                return pickerVm.ConfirmSelection();
            });
        await viewModel.InitializeAsync();

        viewModel.OpenProcessPickerCommand.Execute(null);

        Assert.NotNull(captured);
        // 三个进程均可选（同路径两个实例都显示）。
        Assert.Equal(3, captured.Rows.Count(r => r.Selectable));

        // 按规范化完整路径去重：同路径只保留一个目标。
        Assert.Equal(2, viewModel.CloseAppsTargets.Count);
        var rowA = Assert.Single(viewModel.CloseAppsTargets, r => ExecutablePathKey.EqualsNormalized(r.ExecutablePath, pathA));
        var rowB = Assert.Single(viewModel.CloseAppsTargets, r => ExecutablePathKey.EqualsNormalized(r.ExecutablePath, pathB));

        // 不持久化 PID 作为长期目标；从进程选择添加绝不授予强杀。
        Assert.Null(rowA.ProcessId);
        Assert.Null(rowB.ProcessId);
        Assert.False(rowA.ForceKillAllowed);
        Assert.False(rowB.ForceKillAllowed);
        // 只读识别信息带入行。
        Assert.Contains("甲产品", rowA.IdentitySummary);
        Assert.Contains("甲公司", rowA.IdentitySummary);
        Assert.True(rowA.HasIdentityInfo);
    }

    [Fact]
    public async Task OpenPicker_ExistingPath_ShowsAlreadyAdded_NotReadded()
    {
        var path = CreateTempFile();
        var config = ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = path, ForceKillAllowed = false });
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 5, sessionId: 1)],
            currentSessionId: 1);
        ProcessPickerViewModel? captured = null;

        var viewModel = CreateViewModel(
            config,
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                captured = pickerVm;
                RefreshSynchronously(pickerVm);
                return pickerVm.ConfirmSelection();
            });
        await viewModel.InitializeAsync();
        Assert.Single(viewModel.CloseAppsTargets);

        viewModel.OpenProcessPickerCommand.Execute(null);

        var row = Assert.Single(captured!.Rows);
        Assert.False(row.Selectable);
        Assert.Equal("已添加", row.UnselectableReason);
        // 目标列表未变。
        Assert.Single(viewModel.CloseAppsTargets);
    }

    [Fact]
    public async Task OpenPicker_Cancel_NoChange()
    {
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("app.exe", CreateTempFile(), pid: 5, sessionId: 1)],
            currentSessionId: 1);

        var viewModel = CreateViewModel(
            ConfigWithoutCloseApps(),
            processInfoProvider: provider,
            processPickerLauncher: _ => null); // 取消。
        await viewModel.InitializeAsync();

        viewModel.OpenProcessPickerCommand.Execute(null);

        Assert.Empty(viewModel.CloseAppsTargets);
    }

    [Fact]
    public async Task Save_PickerAddedTarget_PersistsPathAndIdentityOnly()
    {
        var path = CreateTempFile();
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 7, sessionId: 1, productName: "产品X", companyName: "公司Y", windowTitle: "窗口Z")],
            currentSessionId: 1);

        var config = new RecordingConfigurationService(ConfigWithoutCloseApps());
        var viewModel = CreateViewModel(
            config,
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                pickerVm.Rows[0].IsSelected = true;
                return pickerVm.ConfirmSelection();
            });
        await viewModel.InitializeAsync();

        viewModel.OpenProcessPickerCommand.Execute(null);
        await viewModel.SaveCloseAppsCommand.ExecuteAsync();

        var saved = Assert.Single(config.SavedConfigs);
        var target = Assert.Single(saved.CloseApps!.Targets);
        Assert.Equal(path, target.ExecutablePath);
        Assert.Null(target.ProcessId); // 绝不持久化 PID。
        Assert.False(target.ForceKillAllowed); // 绝不授予强杀。
        Assert.Equal("app.exe", target.ProcessName);
        Assert.Equal("产品X", target.ProductName);
        Assert.Equal("公司Y", target.CompanyName);
        Assert.Equal("窗口Z", target.WindowTitleAtAdd);
        Assert.Equal(Now, target.AddedAtUtc);
    }

    [Fact]
    public async Task SaveReload_IdentityRoundTrip()
    {
        var path = CreateTempFile();
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 7, sessionId: 1, productName: "产品X", companyName: "公司Y")],
            currentSessionId: 1);
        var config = new RecordingConfigurationService(ConfigWithoutCloseApps());
        var viewModel = CreateViewModel(
            config,
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                pickerVm.Rows[0].IsSelected = true;
                return pickerVm.ConfirmSelection();
            });
        await viewModel.InitializeAsync();

        viewModel.OpenProcessPickerCommand.Execute(null);
        await viewModel.SaveCloseAppsCommand.ExecuteAsync();

        // 保存后再刷新，识别信息与添加时间往返一致。
        await viewModel.RefreshConfigurationAsync();
        var row = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Contains("产品X", row.IdentitySummary);
        Assert.Contains("公司Y", row.IdentitySummary);
        Assert.NotEqual(string.Empty, row.AddedAtDisplay);
    }

    [Fact]
    public async Task Load_InvalidPath_ShowsPathInvalidHint_AndReSelectReplaces()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "as-closeui1-gone-" + Guid.NewGuid().ToString("N") + ".exe");
        var validPath = CreateTempFile();
        var config = ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = missingPath, ForceKillAllowed = false });
        var provider = new FakeProcessInfoProvider(
            [RunningProcess("replacement.exe", validPath, pid: 9, sessionId: 1, productName: "替代程序")],
            currentSessionId: 1);

        var viewModel = CreateViewModel(
            config,
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                var row = pickerVm.Rows.Single(r => r.Selectable);
                row.IsSelected = true;
                return pickerVm.ConfirmSelection();
            });
        await viewModel.InitializeAsync();

        // 升级场景：原程序路径已失效 → 行标失效 + 提示 + 重新选择入口。
        var invalidRow = Assert.Single(viewModel.CloseAppsTargets);
        Assert.True(invalidRow.IsPathInvalid);
        Assert.Equal("原程序路径已失效，请重新选择运行中的程序", CloseAppsTargetRow.PathInvalidHintText);

        // 用户重新选择运行中的程序 → 失效行被替换。
        viewModel.ReSelectCloseAppsTargetCommand.Execute(invalidRow);

        var replaced = Assert.Single(viewModel.CloseAppsTargets);
        Assert.False(replaced.IsPathInvalid);
        Assert.Equal(validPath, replaced.ExecutablePath);
        Assert.Contains("替代程序", replaced.IdentitySummary);
    }

    [Fact]
    public async Task Load_ValidPath_NotInvalid()
    {
        var validPath = CreateTempFile();
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = validPath, ForceKillAllowed = false }));

        await viewModel.InitializeAsync();

        var row = Assert.Single(viewModel.CloseAppsTargets);
        Assert.False(row.IsPathInvalid);
    }

    [Fact]
    public async Task ReSelect_Cancel_KeepsInvalidRow()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "as-closeui1-gone-" + Guid.NewGuid().ToString("N") + ".exe");
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = missingPath, ForceKillAllowed = false }),
            processInfoProvider: new FakeProcessInfoProvider([], currentSessionId: 1),
            processPickerLauncher: _ => null); // 重新选择被取消。
        await viewModel.InitializeAsync();

        var invalidRow = Assert.Single(viewModel.CloseAppsTargets);
        Assert.True(invalidRow.IsPathInvalid);

        viewModel.ReSelectCloseAppsTargetCommand.Execute(invalidRow);

        Assert.Single(viewModel.CloseAppsTargets); // 未改动。
    }

    // ==================== 夹具 ====================

    private string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "as-closeui1-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "placeholder");
        _tempFiles.Add(path);
        return path;
    }

    private static string NotepadPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    private static RunningProcessInfo RunningProcess(
        string processName,
        string? executablePath,
        int pid,
        int sessionId,
        string windowTitle = "",
        string productName = "",
        string companyName = "") => new()
        {
            ProcessId = pid,
            ProcessName = processName,
            ExecutablePath = executablePath,
            SessionId = sessionId,
            WindowTitle = windowTitle,
            ProductName = productName,
            CompanyName = companyName
        };

    private static ProcessSelectionContext Context(int sessionId = 1)
        => new() { CurrentProcessId = 99999, CurrentSessionId = sessionId, CurrentExecutablePath = null };

    private static void RefreshSynchronously(ProcessPickerViewModel picker)
        => picker.RefreshAsync().GetAwaiter().GetResult();

    private static AppConfig ConfigWithoutCloseApps() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static AppConfig ConfigWithCloseApps(params CloseAppsTargetConfig[] targets) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        CloseApps = new CloseAppsConfig { GracefulTimeoutSeconds = 30, Targets = targets }
    };

    private static MainWindowViewModel CreateViewModel(
        AppConfig config,
        IProcessInfoProvider? processInfoProvider = null,
        Func<ProcessPickerViewModel, ProcessPickerResult?>? processPickerLauncher = null)
        => CreateViewModel(
            new RecordingConfigurationService(config),
            processInfoProvider,
            processPickerLauncher);

    private static MainWindowViewModel CreateViewModel(
        RecordingConfigurationService config,
        IProcessInfoProvider? processInfoProvider = null,
        Func<ProcessPickerViewModel, ProcessPickerResult?>? processPickerLauncher = null)
        => new(
            new RunningEngine(),
            config,
            new FixedClock(Now),
            new NullLogger(),
            new FakeAutoStartService(),
            autoStartConfirmation: () => true,
            cancelConfirmation: () => true,
            realPowerConfirmation: () => true,
            processInfoProvider: processInfoProvider,
            processPickerLauncher: processPickerLauncher);

    private sealed class RunningEngine : ISchedulerEngine
    {
        public SchedulerSnapshot GetSnapshot() => new()
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = new Dictionary<Guid, TaskInstance>(),
            LastUpdatedAt = default
        };

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
            => Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = GetSnapshot(),
                Message = "ok"
            });
    }

    private sealed class RecordingConfigurationService : IConfigurationService
    {
        private AppConfig _config;

        public RecordingConfigurationService(AppConfig config) => _config = config;

        public List<AppConfig> SavedConfigs { get; } = [];

        public ConfigurationLoadStatus LoadStatus { get; set; } = ConfigurationLoadStatus.Success;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = LoadStatus,
                Config = LoadStatus == ConfigurationLoadStatus.Success ? _config : null
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
        {
            SavedConfigs.Add(config);
            _config = config;
            return Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
        }

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public AutoStartStatus GetStatus() => AutoStartStatus.Disabled;

        public AutoStartOperationResult Enable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class NullLogger : IApplicationLogger
    {
        public string LogDirectory => "C:\\logs";

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(ApplicationLogLevel level, string eventName, string message, Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 只读进程信息替身：测试专用，绝不枚举/启动/关闭真实进程。
    /// 可注入枚举异常、GetById 返回 null（进程已退出）、路径变化等复核场景。
    /// </summary>
    private sealed class FakeProcessInfoProvider : IProcessInfoProvider
    {
        private readonly IReadOnlyList<RunningProcessInfo> _processes;

        public FakeProcessInfoProvider(
            IReadOnlyList<RunningProcessInfo> processes,
            int currentProcessId = 99999,
            int currentSessionId = 1,
            string? currentExecutablePath = null)
        {
            _processes = processes;
            CurrentProcessId = currentProcessId;
            CurrentSessionId = currentSessionId;
            CurrentExecutablePath = currentExecutablePath;
        }

        public int CurrentProcessId { get; }

        public int CurrentSessionId { get; }

        public string? CurrentExecutablePath { get; }

        public int GetByIdCalls { get; private set; }

        public bool ThrowOnEnumerate { get; set; }

        public bool GetByIdReturnsNull { get; set; }

        public string? GetByIdPathOverride { get; set; }

        public IReadOnlyList<RunningProcessInfo> EnumerateProcesses()
        {
            if (ThrowOnEnumerate)
            {
                throw new InvalidOperationException("simulated enumeration failure");
            }

            return _processes;
        }

        public RunningProcessInfo? GetById(int processId)
        {
            GetByIdCalls++;
            if (GetByIdReturnsNull)
            {
                return null;
            }

            var info = _processes.FirstOrDefault(p => p.ProcessId == processId);
            if (info is null)
            {
                return null;
            }

            return GetByIdPathOverride is null ? info : info with { ExecutablePath = GetByIdPathOverride };
        }
    }
}
