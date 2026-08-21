using System.Diagnostics;
using System.Text.RegularExpressions;
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
/// S-CLOSEUI1-D1 聚焦测试（最小返修）。覆盖十一项要求的自动化场景：
/// 一 重新选择误删修复（空确认/取消/全部复核失败保持原目标；多选拒绝；原子替换）；
/// 二 完整祖先链 reparse 检查（junction\app.exe、祖先不可读 fail-closed、正常祖先链放行、终文件仍须普通）；
/// 三 规范化路径保存一致性（大小写/./.. /重复路径去重、规范化失败不入列）；
/// 四 筛选与按钮（搜索多字段、窗口过滤默认开、可选过滤默认开、全选可见+可选、重选禁用、
///    取消当前仅可见、取消全部全清、已选数量跨筛选、刷新选择保留/清除提示）；
/// 五 不可用原因（逐类行为验证 + 源码至少 11 类）；
/// 六 确定前摘要（仅含复核通过去重项、返回修改/取消不修改集合、重选摘要原/新路径）；
/// 七 进程身份复核（PID+启动时间+路径三重确认；任一未知/不一致 fail-closed）；
/// 八 安全边界（只读源码契约、强杀仍 false、持久化仍无 PID、无进程名兜底）。
/// 全程使用替身进程信息提供器与替身窗口启动器，绝不枚举/启动/关闭真实进程。
/// </summary>
public sealed class S_CLOSEUI1_D1_ProcessPickerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2024, 2, 1, 9, 30, 0, TimeSpan.Zero);

    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // 清理失败不影响测试结论。
            }
        }
    }

    // ==================== 一、重新选择误删修复 ====================

    [Fact]
    public async Task ReSelect_NothingChecked_PreviewNull_OriginalKept()
    {
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("new.exe", pathB, pid: 10, sessionId: 1)],
            currentSessionId: 1);
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = pathA, ForceKillAllowed = false }),
            processInfoProvider: provider,
            processPickerLauncher: _ => null); // 空确认（未勾选）→ 摘要 null → 等同取消。
        await viewModel.InitializeAsync();

        var original = Assert.Single(viewModel.CloseAppsTargets);
        viewModel.ReSelectCloseAppsTargetCommand.Execute(original);

        // 空确认：原目标必须保持不变（绝不误删）。
        var kept = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Equal(pathA, kept.ExecutablePath);
        Assert.False(kept.IsPathInvalid);
    }

    [Fact]
    public async Task ReSelect_AllFailRecheck_OriginalKept()
    {
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("new.exe", pathB, pid: 10, sessionId: 1)],
            currentSessionId: 1)
        {
            GetByIdReturnsNull = true // 摘要前复核：进程已退出。
        };
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = pathA, ForceKillAllowed = false }),
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                pickerVm.Rows[0].IsSelected = true;
                return ConfirmViaPreview(pickerVm); // 全部复核失败 → null。
            });
        await viewModel.InitializeAsync();

        var original = Assert.Single(viewModel.CloseAppsTargets);
        viewModel.ReSelectCloseAppsTargetCommand.Execute(original);

        // 全部复核失败：原目标保持不变。
        var kept = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Equal(pathA, kept.ExecutablePath);
    }

    [Fact]
    public void ReSelect_MultipleChecked_PreviewNull_WarnsSingleOnly()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("one.exe", CreateTempFile(), pid: 1, sessionId: 1),
                RunningProcess("two.exe", CreateTempFile(), pid: 2, sessionId: 1)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(
            provider,
            Context(sessionId: 1),
            isReSelectMode: true,
            reselectOriginalPath: @"C:\gone\old.exe");

        RefreshSynchronously(picker);
        foreach (var row in picker.Rows)
        {
            row.IsSelected = true; // 多选。
        }

        var preview = picker.BuildPreview();

        Assert.Null(preview);
        Assert.Contains("重新选择一次只能选择一个程序", picker.WarningText);
    }

    [Fact]
    public async Task ReSelect_ConfirmNew_AtomicallyReplacesOnlyThatRow()
    {
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();
        var pathC = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("replacement.exe", pathC, pid: 30, sessionId: 1, productName: "替代程序")],
            currentSessionId: 1);
        var viewModel = CreateViewModel(
            ConfigWithCloseApps(
                new CloseAppsTargetConfig { ExecutablePath = pathA, ForceKillAllowed = false },
                new CloseAppsTargetConfig { ExecutablePath = pathB, ForceKillAllowed = false }),
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                pickerVm.Rows[0].IsSelected = true;
                return ConfirmViaPreview(pickerVm);
            });
        await viewModel.InitializeAsync();

        var rowA = Assert.Single(viewModel.CloseAppsTargets, r => ExecutablePathKey.EqualsNormalized(r.ExecutablePath, pathA));
        viewModel.ReSelectCloseAppsTargetCommand.Execute(rowA);

        // 原子替换：pathA 被 pathC 替换，pathB 不受影响。
        Assert.Equal(2, viewModel.CloseAppsTargets.Count);
        Assert.Contains(viewModel.CloseAppsTargets, r => ExecutablePathKey.EqualsNormalized(r.ExecutablePath, pathC));
        Assert.Contains(viewModel.CloseAppsTargets, r => ExecutablePathKey.EqualsNormalized(r.ExecutablePath, pathB));
        Assert.DoesNotContain(viewModel.CloseAppsTargets, r => ExecutablePathKey.EqualsNormalized(r.ExecutablePath, pathA));
        var newRow = Assert.Single(viewModel.CloseAppsTargets, r => ExecutablePathKey.EqualsNormalized(r.ExecutablePath, pathC));
        Assert.Contains("替代程序", newRow.IdentitySummary);
    }

    [Fact]
    public void ReSelect_Summary_ShowsOriginalAndNewPath_ConfirmButtonText()
    {
        var originalPath = @"C:\gone\old.exe";
        var newPath = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("new.exe", newPath, pid: 5, sessionId: 1)],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(
            provider,
            Context(sessionId: 1),
            isReSelectMode: true,
            reselectOriginalPath: originalPath);

        RefreshSynchronously(picker);
        picker.Rows[0].IsSelected = true;
        var preview = picker.BuildPreview();

        Assert.NotNull(preview);
        Assert.True(preview.IsReSelectMode);
        Assert.Equal(originalPath, preview.OriginalPath);
        Assert.Equal("确认替换", preview.ConfirmButtonText);
        Assert.Contains(originalPath, preview.OriginalPath ?? string.Empty);
        Assert.Equal(newPath, preview.NewPathText);
    }

    // ==================== 二、祖先链 reparse 检查 ====================

    [SkippableFact]
    public void Guard_Junction_SubpathExe_NotSelectable_OrHonestSkip()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "as-d1-junction-sub-" + Guid.NewGuid().ToString("N"));
        var realDir = Path.Combine(baseDir, "real");
        var link = Path.Combine(baseDir, "link");
        var junctionApp = Path.Combine(link, "app.exe"); // junction\app.exe（不是 junction 目录本身）。
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "app.exe"), "placeholder");
        try
        {
            var created = TryCreateJunction(link, realDir);
            // D1 要求：无法创建 junction 时明确 SKIP，绝不假通过。
            Skip.IfNot(created, "无法创建 junction（本机环境不支持 mklink /J），测试跳过而非假通过");

            var decision = ProcessSelectionGuard.Evaluate(
                RunningProcess("app.exe", junctionApp, pid: 10, sessionId: 1),
                Context(sessionId: 1));

            // 祖先目录是 junction → 即使 EXE 文件自身不带 ReparsePoint 也 fail-closed。
            Assert.False(decision.Selectable);
            Assert.Contains("祖先目录", decision.Reason);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Guard_AncestorUnreadable_FailClosed()
    {
        // 注入读取器：EXE 自身正常，但某一级祖先属性读取抛访问拒绝 → 必须 fail-closed。
        var exe = @"C:\app\bin\sub\app.exe";
        var decision = ProcessSelectionGuard.CheckOrdinaryFileForTest(
            exe,
            existsReader: _ => true,
            attributeReader: path =>
            {
                if (string.Equals(path, @"C:\app\bin", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("simulated access denied");
                }

                return string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Normal // 终文件是普通文件。
                    : FileAttributes.Directory | FileAttributes.Normal; // 祖先目录。
            });

        Assert.NotNull(decision);
        Assert.False(decision.Selectable);
        Assert.Contains("无法确认", decision.Reason);
    }

    [Fact]
    public void Guard_AncestorNotFound_FailClosed()
    {
        // 祖先目录属性读取返回「非目录」→ 路径结构无法确认 → fail-closed。
        var decision = ProcessSelectionGuard.CheckOrdinaryFileForTest(
            @"C:\app\bin\app.exe",
            existsReader: _ => true,
            attributeReader: path => path is "app.exe" or @"C:\app\bin\app.exe"
                ? FileAttributes.Normal
                : FileAttributes.Normal); // 全部非目录：结构异常。

        Assert.NotNull(decision);
        Assert.False(decision.Selectable);
    }

    [Fact]
    public void Guard_AncestorChainNormal_Selectable()
    {
        // 多层普通目录祖先链 → 放行（null = 通过文件检查）。
        var exe = @"C:\app\bin\sub\deeper\app.exe";
        var decision = ProcessSelectionGuard.CheckOrdinaryFileForTest(
            exe,
            existsReader: _ => true,
            attributeReader: path => string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Normal // 终文件是普通文件。
                : FileAttributes.Directory | FileAttributes.Normal); // 祖先目录。

        Assert.Null(decision); // 通过（后续由 Evaluate 继续判定会话等）。
    }

    [Fact]
    public void Guard_AncestorReparse_NotSelectable()
    {
        var decision = ProcessSelectionGuard.CheckOrdinaryFileForTest(
            @"C:\app\link\app.exe",
            existsReader: _ => true,
            attributeReader: path =>
                string.Equals(path, @"C:\app\link", StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.ReparsePoint | FileAttributes.Directory
                    : FileAttributes.Normal);

        Assert.NotNull(decision);
        Assert.False(decision.Selectable);
        Assert.Contains("祖先目录", decision.Reason);
    }

    [Fact]
    public void Guard_FinalExeStillOrdinary_ReparseExe_NotSelectable()
    {
        // 终文件自身 reparse → 不可选择（即使祖先链干净）。
        var decision = ProcessSelectionGuard.CheckOrdinaryFileForTest(
            @"C:\app\bin\app.exe",
            existsReader: _ => true,
            attributeReader: path =>
                string.Equals(path, @"C:\app\bin\app.exe", StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.ReparsePoint
                    : FileAttributes.Directory | FileAttributes.Normal);

        Assert.NotNull(decision);
        Assert.False(decision.Selectable);
        Assert.Contains("符号链接", decision.Reason);
    }

    // ==================== 三、规范化路径保存一致性 ====================

    [Fact]
    public async Task Save_NormalizedPath_StoredAsFullPath()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "as-d1-norm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "sub"));
        _tempPaths.Add(baseDir);
        var realPath = Path.Combine(baseDir, "app.exe");
        File.WriteAllText(realPath, "placeholder");
        _tempPaths.Add(realPath);
        var raggedPath = Path.Combine(baseDir, "sub", "..", "app.exe"); // 带 . 与 .. 段。

        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("app.exe", raggedPath, pid: 7, sessionId: 1)],
            currentSessionId: 1);
        var config = new D1RecordingConfigurationService(ConfigWithoutCloseApps());
        var viewModel = CreateViewModel(
            config,
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                pickerVm.Rows[0].IsSelected = true;
                return ConfirmViaPreview(pickerVm);
            });
        await viewModel.InitializeAsync();

        viewModel.OpenProcessPickerCommand.Execute(null);
        await viewModel.SaveCloseAppsCommand.ExecuteAsync();

        var target = Assert.Single(Assert.Single(config.SavedConfigs).CloseApps!.Targets);
        Assert.Equal(realPath, target.ExecutablePath); // 保存的是规范化完整路径，非带 .. 的原始路径。
    }

    [Fact]
    public void Guard_InvalidPathChars_NormalizationFails_NotSelectable()
    {
        // 含 NUL 的路径无法规范化（GetFullPath 抛 ArgumentException）→ fail-closed 不可选，
        // 从而不可能被加入目标列表（规范化失败不添加）。注意：必须用普通字符串写出真正的 NUL。
        var decision = ProcessSelectionGuard.Evaluate(
            RunningProcess("app.exe", "C:\\app\u0000bad.exe", pid: 10, sessionId: 1),
            Context(sessionId: 1));

        Assert.False(decision.Selectable);
        Assert.Contains("规范化", decision.Reason);
    }

    [Fact]
    public async Task OpenPicker_DuplicatePaths_DifferentCaseAndDotSegments_DedupToSingle()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "as-d1-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "sub"));
        _tempPaths.Add(baseDir);
        var realPath = Path.Combine(baseDir, "App.EXE");
        File.WriteAllText(realPath, "placeholder");
        _tempPaths.Add(realPath);
        var upperPath = realPath.ToUpperInvariant(); // 同一文件，路径大小写不同。
        var raggedPath = Path.Combine(baseDir, "sub", "..", realPath); // 同一文件，带 .. 段。

        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("app.exe", realPath, pid: 1, sessionId: 1),
                RunningProcess("app.exe", upperPath, pid: 2, sessionId: 1),
                RunningProcess("app.exe", raggedPath, pid: 3, sessionId: 1)
            ],
            currentSessionId: 1);
        var viewModel = CreateViewModel(
            ConfigWithoutCloseApps(),
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                foreach (var row in pickerVm.Rows.Where(r => r.Selectable))
                {
                    row.IsSelected = true;
                }

                return ConfirmViaPreview(pickerVm);
            });
        await viewModel.InitializeAsync();

        viewModel.OpenProcessPickerCommand.Execute(null);

        // 三个进程指向同一规范化路径 → 去重后只有一个目标。
        var target = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Equal(realPath, target.ExecutablePath);
    }

    // ==================== 四、筛选与按钮 ====================

    [Fact]
    public async Task Filter_SearchMatchesNamePathTitleProductCompany()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("notepad.exe", CreateTempFile(), pid: 1, sessionId: 1, windowTitle: "文档 - 记事本"),
                RunningProcess("calc.exe", CreateTempFile(), pid: 2, sessionId: 1, productName: "计算器"),
                RunningProcess("paint.exe", CreateTempFile(), pid: 3, sessionId: 1, companyName: "微软"),
                RunningProcess("other.exe", CreateTempFile(), pid: 4, sessionId: 1)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        // 进程名。
        picker.SearchText = "note";
        Assert.Single(picker.Rows);
        Assert.Equal(1, picker.Rows[0].Info.ProcessId);
        // 路径（大小写不敏感）。
        picker.SearchText = "OTHER.EXE";
        Assert.Single(picker.Rows);
        Assert.Equal(4, picker.Rows[0].Info.ProcessId);
        // 窗口标题。
        picker.SearchText = "记事本";
        Assert.Single(picker.Rows);
        Assert.Equal(1, picker.Rows[0].Info.ProcessId);
        // 产品名。
        picker.SearchText = "计算器";
        Assert.Single(picker.Rows);
        Assert.Equal(2, picker.Rows[0].Info.ProcessId);
        // 公司名。
        picker.SearchText = "微软";
        Assert.Single(picker.Rows);
        Assert.Equal(3, picker.Rows[0].Info.ProcessId);
        // 无命中。
        picker.SearchText = "不存在xyz";
        Assert.Empty(picker.Rows);
    }

    [Fact]
    public async Task Filter_WindowedOnly_DefaultOn_HidesWindowless()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("withwin.exe", CreateTempFile(), pid: 1, sessionId: 1, hasMainWindow: true),
                RunningProcess("noWindow.exe", CreateTempFile(), pid: 2, sessionId: 1, hasMainWindow: false)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        Assert.True(picker.ShowWindowedOnly); // 默认开启。
        var visible = Assert.Single(picker.Rows);
        Assert.Equal(1, visible.Info.ProcessId);

        picker.ShowWindowedOnly = false;
        Assert.Equal(2, picker.Rows.Count);
    }

    [Fact]
    public async Task Filter_SelectableOnly_DefaultOn_OffShowsReasons()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("good.exe", CreateTempFile(), pid: 1, sessionId: 1),
                RunningProcess("svchost", CreateTempFile(), pid: 2, sessionId: 1) // 系统关键进程。
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        Assert.True(picker.ShowSelectableOnly); // 默认开启。
        var visible = Assert.Single(picker.Rows);
        Assert.Equal(1, visible.Info.ProcessId);

        picker.ShowSelectableOnly = false;
        Assert.Equal(2, picker.Rows.Count);
        var system = Assert.Single(picker.Rows, r => r.Info.ProcessId == 2);
        Assert.False(system.Selectable);
        Assert.Contains("系统关键进程", system.UnselectableReason);
    }

    [Fact]
    public async Task SelectAllVisible_SelectsOnlyVisibleSelectable_NotHiddenOrUnselectable()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("a.exe", CreateTempFile(), pid: 1, sessionId: 1, hasMainWindow: true),
                RunningProcess("b.exe", CreateTempFile(), pid: 2, sessionId: 1, hasMainWindow: true),
                RunningProcess("hidden.exe", CreateTempFile(), pid: 3, sessionId: 1, hasMainWindow: false), // 窗口筛选隐藏。
                RunningProcess("svchost", CreateTempFile(), pid: 4, sessionId: 1) // 不可选。
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.SelectAllVisible();

        // 只有可见且可选的两行被选中；被筛选隐藏的可选行与不可选行均不被选中。
        Assert.Equal(2, picker.SelectedCount);
        Assert.True(picker.Rows[0].IsSelected);
        Assert.True(picker.Rows[1].IsSelected);
    }

    [Fact]
    public void SelectAllVisible_Disabled_InReselectMode()
    {
        var picker = new ProcessPickerViewModel(
            new D1FakeProcessInfoProvider([], currentSessionId: 1),
            Context(sessionId: 1),
            isReSelectMode: true);

        Assert.False(picker.SelectAllVisibleCommand.CanExecute(null));
        picker.SelectAllVisible(); // 双保险：即使直接调用也不选任何行。
        Assert.Equal(0, picker.SelectedCount);
    }

    [Fact]
    public async Task ClearCurrent_OnlyClearsVisibleRows_KeepsHiddenSelection()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("a.exe", CreateTempFile(), pid: 1, sessionId: 1, hasMainWindow: true),
                RunningProcess("b.exe", CreateTempFile(), pid: 2, sessionId: 1, hasMainWindow: false)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.ShowWindowedOnly = false; // 先全显再全选。
        picker.SelectAllVisible();
        Assert.Equal(2, picker.SelectedCount);

        picker.ShowWindowedOnly = true; // 隐藏 b 行。
        picker.ClearCurrent();

        // b（被筛选隐藏）保持勾选；a（可见）被取消。
        Assert.Equal(1, picker.SelectedCount);
        Assert.False(picker.Rows.Single().IsSelected);
    }

    [Fact]
    public async Task ClearAll_ClearsAcrossFilters()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("a.exe", CreateTempFile(), pid: 1, sessionId: 1, hasMainWindow: true),
                RunningProcess("b.exe", CreateTempFile(), pid: 2, sessionId: 1, hasMainWindow: false)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.ShowWindowedOnly = false;
        picker.SelectAllVisible();
        Assert.Equal(2, picker.SelectedCount);

        picker.ShowWindowedOnly = true; // 隐藏 b 行后仍应被清除。
        picker.ClearAll();

        Assert.Equal(0, picker.SelectedCount);
    }

    [Fact]
    public async Task SelectedCount_TracksAcrossSearchAndFilterChanges()
    {
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("alpha.exe", CreateTempFile(), pid: 1, sessionId: 1),
                RunningProcess("beta.exe", CreateTempFile(), pid: 2, sessionId: 1),
                RunningProcess("gamma.exe", CreateTempFile(), pid: 3, sessionId: 1)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.SelectAllVisible();
        Assert.Equal(3, picker.SelectedCount);

        // 搜索缩小可见范围，已选数量不受影响。
        picker.SearchText = "alpha";
        Assert.Single(picker.Rows);
        Assert.Equal(3, picker.SelectedCount);

        // 再单独取消 alpha，其余仍保留。
        picker.Rows[0].IsSelected = false;
        Assert.Equal(2, picker.SelectedCount);

        picker.SearchText = string.Empty;
        Assert.Equal(3, picker.Rows.Count);
        Assert.Equal(2, picker.SelectedCount);
    }

    [Fact]
    public async Task Refresh_KeepsSelectionForIdentifiedSamePidStartTimePath()
    {
        var path = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 1, sessionId: 1)],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.SelectAllVisible();
        Assert.Equal(1, picker.SelectedCount);

        // 刷新后 PID + 启动时间 + 路径三重确认仍一致 → 选择保留。
        await picker.RefreshAsync();

        Assert.Equal(1, picker.SelectedCount);
        Assert.True(picker.Rows.Single().IsSelected);
        Assert.DoesNotContain("刷新后已清除", picker.WarningText);
    }

    [Fact]
    public async Task Refresh_DropsUnconfirmedSelection_WarnsCleared()
    {
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("a.exe", pathA, pid: 1, sessionId: 1),
                RunningProcess("b.exe", pathB, pid: 2, sessionId: 1)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.SelectAllVisible();
        Assert.Equal(2, picker.SelectedCount);

        // b 退出（枚举不再包含）→ 刷新后其选择被清除并提示；a 保留。
        provider.ReplaceProcesses([RunningProcess("a.exe", pathA, pid: 1, sessionId: 1)]);
        await picker.RefreshAsync();

        Assert.Equal(1, picker.SelectedCount);
        Assert.Contains("刷新后已清除 1 项无法确认的原选择", picker.WarningText);
        Assert.Equal(1, picker.Rows.Single().Info.ProcessId);
    }

    // ==================== 五、不可用原因逐类 ====================

    [Fact]
    public void UnselectableReasons_BehavioralClasses_FailClosed()
    {
        var existingPath = CreateTempFile();
        var context = new ProcessSelectionContext
        {
            CurrentProcessId = 99999,
            CurrentSessionId = 1,
            CurrentExecutablePath = null,
            ExistingTargetPaths = [existingPath]
        };

        var missing = Path.Combine(Path.GetTempPath(), "as-d1-missing-" + Guid.NewGuid().ToString("N") + ".exe");

        AssertUnselectable("自身进程", RunningProcess("app.exe", CreateTempFile(), pid: 99999, sessionId: 1), context, "自身");
        AssertUnselectable("路径不可读", RunningProcess("app.exe", null, pid: 2, sessionId: 1), context, "完整路径");
        AssertUnselectable("路径不存在", RunningProcess("app.exe", missing, pid: 3, sessionId: 1), context, "不存在");
        AssertUnselectable("辅助进程", RunningProcess("AutoShutdown.OfficeSaveHelper", CreateTempFile(), pid: 4, sessionId: 1), context, "辅助进程");
        AssertUnselectable("系统关键进程", RunningProcess("svchost", CreateTempFile(), pid: 5, sessionId: 1), context, "系统关键");
        AssertUnselectable("其他会话", RunningProcess("app.exe", CreateTempFile(), pid: 6, sessionId: 3), context, "会话");
        AssertUnselectable("会话未知", RunningProcess("app.exe", CreateTempFile(), pid: 7, sessionId: -1), context, "会话");
        AssertUnselectable("已添加", RunningProcess("app.exe", existingPath, pid: 8, sessionId: 1), context, "已添加");
        AssertUnselectable("相对路径", RunningProcess("app.exe", "app.exe", pid: 9, sessionId: 1), context, "绝对路径");
    }

    [Fact]
    public void UnselectableReasons_Source_AtLeastElevenDistinctClasses()
    {
        var guard = File.ReadAllText(SourcePath("Infrastructure", "ProcessSelection", "ProcessSelectionGuard.cs"));
        var reasons = Regex.Matches(guard, @"NotSelectable\(""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // D1 要求：至少 11 类明确不可用原因。
        Assert.True(reasons.Count >= 11, $"仅找到 {reasons.Count} 类不可选原因");
    }

    // ==================== 六、确定前摘要 ====================

    [Fact]
    public async Task Preview_ShowsOnlyRecheckedDedupedItems()
    {
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [
                RunningProcess("one.exe", pathA, pid: 1, sessionId: 1),
                RunningProcess("two.exe", pathA, pid: 2, sessionId: 1, windowTitle: "第二个实例"),
                RunningProcess("three.exe", pathB, pid: 3, sessionId: 1)
            ],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        foreach (var row in picker.Rows)
        {
            row.IsSelected = true;
        }

        var preview = picker.BuildPreview();

        Assert.NotNull(preview);
        Assert.Equal(2, preview.FinalCount); // 同路径两个实例去重（保留首个确认项）。
        Assert.Equal(2, preview.ConfirmedProcesses.Count);
        Assert.Equal(2, preview.Items.Count);
        // 去重后 pathA 只保留一个条目、pathB 保留一个条目。
        Assert.Contains(preview.Items, i => ExecutablePathKey.EqualsNormalized(i.ExecutablePath, pathA));
        Assert.Contains(preview.Items, i => ExecutablePathKey.EqualsNormalized(i.ExecutablePath, pathB));
        Assert.DoesNotContain(preview.Items, i => i.WindowTitle == "第二个实例"); // 同路径重复实例被去重。
    }

    [Fact]
    public async Task BuildPreview_Failed_PreviewNull_RowsStayChecked_WindowStays()
    {
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("good.exe", CreateTempFile(), pid: 1, sessionId: 1)],
            currentSessionId: 1)
        {
            GetByIdReturnsNull = true // 复核时已退出。
        };
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var preview = picker.BuildPreview();

        Assert.Null(preview); // 停留窗口。
        Assert.True(picker.Rows[0].IsSelected); // 勾选保留，便于返回修改。

        // 取消：清除警告，不修改任何勾选。
        picker.Cancel();
        Assert.False(picker.HasWarnings);
        Assert.True(picker.Rows[0].IsSelected);
    }

    [Fact]
    public void Preview_OriginalPathOnlyShownInReselectMode()
    {
        var path = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 1, sessionId: 1)],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        RefreshSynchronously(picker);
        picker.Rows[0].IsSelected = true;

        var normal = picker.BuildPreview();
        Assert.NotNull(normal);
        Assert.False(normal.ShowOriginalPath);
    }

    // ==================== 七、进程身份复核 ====================

    [Fact]
    public async Task Identity_StartTimeChanged_PidReused_Rejected()
    {
        var path = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 1, sessionId: 1, startTimeUtc: Now)],
            currentSessionId: 1)
        {
            GetByIdStartTimeOverride = Now.AddHours(1) // PID 复用 / 身份变化。
        };
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var preview = picker.BuildPreview();

        Assert.Null(preview);
        Assert.Contains("启动时间不一致", picker.WarningText);
    }

    [Fact]
    public async Task Identity_RowStartTimeUnknown_Rejected()
    {
        var path = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 1, sessionId: 1, startTimeUtc: default(DateTimeOffset))],
            currentSessionId: 1);
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var preview = picker.BuildPreview();

        Assert.Null(preview);
        Assert.Contains("启动时间未知", picker.WarningText);
    }

    [Fact]
    public async Task Identity_FreshStartTimeUnknown_ProtectedProcess_Rejected_NoBypass()
    {
        var path = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("app.exe", path, pid: 1, sessionId: 1)],
            currentSessionId: 1)
        {
            GetByIdStartTimeUnknown = true // 受保护进程无法读取启动时间。
        };
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var preview = picker.BuildPreview();

        // 绝无降级绕过：无法确认启动时间一律 fail-closed。
        Assert.Null(preview);
        Assert.Contains("无法确认启动时间", picker.WarningText);
    }

    [Fact]
    public async Task Identity_FreshPathChanged_Rejected()
    {
        var original = CreateTempFile();
        var changed = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("app.exe", original, pid: 1, sessionId: 1)],
            currentSessionId: 1)
        {
            GetByIdPathOverride = changed
        };
        var picker = new ProcessPickerViewModel(provider, Context(sessionId: 1));
        await picker.RefreshAsync();

        picker.Rows[0].IsSelected = true;
        var preview = picker.BuildPreview();

        Assert.Null(preview);
        Assert.Contains("路径已变化", picker.WarningText);
    }

    // ==================== 八、安全边界与执行契约 ====================

    [Fact]
    public async Task Reselect_ConfirmedAdd_StillNoForceKill_NoPidPersisted()
    {
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();
        var provider = new D1FakeProcessInfoProvider(
            [RunningProcess("new.exe", pathB, pid: 20, sessionId: 1)],
            currentSessionId: 1);
        var config = new D1RecordingConfigurationService(
            ConfigWithCloseApps(new CloseAppsTargetConfig { ExecutablePath = pathA, ForceKillAllowed = false }));
        var viewModel = CreateViewModel(
            config,
            processInfoProvider: provider,
            processPickerLauncher: pickerVm =>
            {
                RefreshSynchronously(pickerVm);
                pickerVm.Rows[0].IsSelected = true;
                return ConfirmViaPreview(pickerVm);
            });
        await viewModel.InitializeAsync();

        var original = Assert.Single(viewModel.CloseAppsTargets);
        viewModel.ReSelectCloseAppsTargetCommand.Execute(original);
        await viewModel.SaveCloseAppsCommand.ExecuteAsync();

        var replaced = Assert.Single(viewModel.CloseAppsTargets);
        Assert.Null(replaced.ProcessId); // 不持久化 PID。
        Assert.False(replaced.ForceKillAllowed); // 强杀仍为 false。

        var saved = Assert.Single(config.SavedConfigs);
        var savedTarget = Assert.Single(saved.CloseApps!.Targets);
        Assert.Equal(pathB, savedTarget.ExecutablePath);
        Assert.Null(savedTarget.ProcessId);
        Assert.False(savedTarget.ForceKillAllowed);
    }

    [Fact]
    public void PickerSources_HaveNoProcessControl_ReadOnly()
    {
        // 安全边界（八）：进程选择只读链路不引入任何进程启动/关闭/终止/窗口消息/网络上传。
        foreach (var file in ProcessSelectionSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("taskkill", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Stop-Process", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Process.Start", content);
            Assert.DoesNotContain("CloseMainWindow", content);
            Assert.DoesNotContain("Kill(", content);
            Assert.DoesNotContain("DllImport", content);
            Assert.DoesNotContain("HttpClient", content);
            Assert.DoesNotContain("Register-ScheduledTask", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExitWindowsEx", content);
            Assert.DoesNotContain("IPowerService", content);
            Assert.DoesNotContain("CloseAppsService", content);
            Assert.DoesNotContain("IProcessManager", content);
        }
    }

    [Fact]
    public void Guard_AncestorCheck_NoFilesystemWriteOperations()
    {
        var guard = File.ReadAllText(SourcePath("Infrastructure", "ProcessSelection", "ProcessSelectionGuard.cs"));

        Assert.DoesNotContain("File.Delete", guard);
        Assert.DoesNotContain("File.Create", guard);
        Assert.DoesNotContain("File.Write", guard);
        Assert.DoesNotContain("File.Append", guard);
        Assert.DoesNotContain("Directory.Create", guard);
        Assert.DoesNotContain("Directory.Delete", guard);
        Assert.DoesNotContain("File.SetAttributes", guard);
        Assert.DoesNotContain("File.Move", guard);
    }

    // ==================== 夹具 ====================

    private static void AssertUnselectable(
        string scenario,
        RunningProcessInfo info,
        ProcessSelectionContext context,
        string reasonFragment)
    {
        var decision = ProcessSelectionGuard.Evaluate(info, context);
        Assert.False(decision.Selectable, scenario + " 应不可选");
        Assert.Contains(reasonFragment, decision.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateJunction(string link, string target)
    {
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
            psi.ArgumentList.Add(target);
            using var process = Process.Start(psi)!;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "as-d1-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "placeholder");
        _tempPaths.Add(path);
        return path;
    }

    private static string SourcePath(params string[] segments)
    {
        var root = FindRoot("AutoShutdown.App");
        foreach (var segment in segments)
        {
            root = Path.Combine(root, segment);
        }

        return root;
    }

    private static IEnumerable<string> ProcessSelectionSources()
    {
        var appRoot = FindRoot("AutoShutdown.App");
        var dir = Path.Combine(appRoot, "Infrastructure", "ProcessSelection");
        return new List<string>(Directory.GetFiles(dir, "*.cs"))
        {
            Path.Combine(appRoot, "Presentation", "ProcessPickerViewModel.cs"),
            Path.Combine(appRoot, "Presentation", "ProcessPickerResult.cs"),
            Path.Combine(appRoot, "Presentation", "ProcessPickerPreview.cs"),
            Path.Combine(appRoot, "Presentation", "RunningProcessRow.cs"),
            Path.Combine(appRoot, "ProcessPickerWindow.xaml.cs"),
            Path.Combine(appRoot, "ProcessPickerSummaryWindow.xaml.cs")
        };
    }

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

    private static RunningProcessInfo RunningProcess(
        string processName,
        string? executablePath,
        int pid,
        int sessionId,
        string windowTitle = "",
        string productName = "",
        string companyName = "",
        bool hasMainWindow = true,
        DateTimeOffset? startTimeUtc = null) => new()
        {
            ProcessId = pid,
            ProcessName = processName,
            ExecutablePath = executablePath,
            SessionId = sessionId,
            WindowTitle = windowTitle,
            ProductName = productName,
            CompanyName = companyName,
            HasMainWindow = hasMainWindow,
            StartTimeUtc = startTimeUtc ?? Now
        };

    private static ProcessSelectionContext Context(int sessionId = 1)
        => new() { CurrentProcessId = 99999, CurrentSessionId = sessionId, CurrentExecutablePath = null };

    private static void RefreshSynchronously(ProcessPickerViewModel picker)
        => picker.RefreshAsync().GetAwaiter().GetResult();

    private static ProcessPickerResult? ConfirmViaPreview(ProcessPickerViewModel picker)
    {
        var preview = picker.BuildPreview();
        return preview is null ? null : picker.Commit(preview);
    }

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
            new D1RecordingConfigurationService(config),
            processInfoProvider,
            processPickerLauncher);

    private static MainWindowViewModel CreateViewModel(
        D1RecordingConfigurationService config,
        IProcessInfoProvider? processInfoProvider = null,
        Func<ProcessPickerViewModel, ProcessPickerResult?>? processPickerLauncher = null)
        => new(
            new D1RunningEngine(),
            config,
            new D1FixedClock(Now),
            new D1NullLogger(),
            new D1FakeAutoStartService(),
            autoStartConfirmation: () => true,
            cancelConfirmation: () => true,
            realPowerConfirmation: () => true,
            processInfoProvider: processInfoProvider,
            processPickerLauncher: processPickerLauncher);

    private sealed class D1RunningEngine : ISchedulerEngine
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

    private sealed class D1RecordingConfigurationService : IConfigurationService
    {
        private AppConfig _config;

        public D1RecordingConfigurationService(AppConfig config) => _config = config;

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

    private sealed class D1FixedClock : IClock
    {
        public D1FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class D1FakeAutoStartService : IAutoStartService
    {
        public AutoStartStatus GetStatus() => AutoStartStatus.Disabled;

        public AutoStartOperationResult Enable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class D1NullLogger : IApplicationLogger
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
    /// D1 只读进程信息替身。可注入枚举异常、GetById 返回 null（进程已退出）、路径/启动时间变化、
    /// 启动时间不可读等复核场景；<see cref="ReplaceProcesses"/> 供「刷新后进程退出」场景。
    /// </summary>
    private sealed class D1FakeProcessInfoProvider : IProcessInfoProvider
    {
        private List<RunningProcessInfo> _processes;

        public D1FakeProcessInfoProvider(
            IReadOnlyList<RunningProcessInfo> processes,
            int currentProcessId = 99999,
            int currentSessionId = 1,
            string? currentExecutablePath = null)
        {
            _processes = new List<RunningProcessInfo>(processes);
            CurrentProcessId = currentProcessId;
            CurrentSessionId = currentSessionId;
            CurrentExecutablePath = currentExecutablePath;
        }

        public int CurrentProcessId { get; }

        public int CurrentSessionId { get; }

        public string? CurrentExecutablePath { get; }

        public bool ThrowOnEnumerate { get; set; }

        public bool GetByIdReturnsNull { get; set; }

        public string? GetByIdPathOverride { get; set; }

        public DateTimeOffset? GetByIdStartTimeOverride { get; set; }

        public bool GetByIdStartTimeUnknown { get; set; }

        public void ReplaceProcesses(IReadOnlyList<RunningProcessInfo> processes)
            => _processes = new List<RunningProcessInfo>(processes);

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
            if (GetByIdReturnsNull)
            {
                return null;
            }

            var info = _processes.FirstOrDefault(p => p.ProcessId == processId);
            if (info is null)
            {
                return null;
            }

            var fresh = GetByIdPathOverride is null ? info : info with { ExecutablePath = GetByIdPathOverride };
            if (GetByIdStartTimeOverride is { } startTime)
            {
                fresh = fresh with { StartTimeUtc = startTime };
            }
            else if (GetByIdStartTimeUnknown)
            {
                fresh = fresh with { StartTimeUtc = default };
            }

            return fresh;
        }
    }
}
