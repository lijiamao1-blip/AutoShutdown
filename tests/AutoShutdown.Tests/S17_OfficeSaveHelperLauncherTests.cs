using System.Diagnostics;
using AutoShutdown.App.Infrastructure.Office;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Office;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using AutoShutdown.OfficeSaveHelper;
using AutoShutdown.OfficeSaveHelper.Office;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S17 独立验收 D2：Office 保存辅助进程启动网关与硬超时的契约与编排测试。
/// 覆盖固定结果协议、路径校验（不存在即不启动）、无 shell 启动、受控参数、
/// 逐应用硬超时（超时/取消终止整棵进程树并传播取消）、结构契约（Process.Start
/// 仅在网关 / 无散落 DllImport / Activator.CreateInstance 不得重现 / Helper 不 Quit）。
/// 全程注入替身启动器与替身进程，绝不启动真实进程、绝不触碰真实 Office。
/// </summary>
public sealed class S17_OfficeSaveHelperLauncherTests
{
    // ---- 固定最小结果协议 ----

    [Fact]
    public void Protocol_RoundTripsAllStatuses()
    {
        foreach (var status in new[]
                 {
                     OfficeAppStatus.Success,
                     OfficeAppStatus.NotDetected,
                     OfficeAppStatus.TimedOut,
                     OfficeAppStatus.PartialFailure
                 })
        {
            var result = new OfficeApplicationSaveResult
            {
                Status = status,
                SavedCount = 2,
                NoPathCount = 1,
                FailedCount = 3
            };

            var line = OfficeSaveHelperProtocol.Format(result);

            Assert.True(OfficeSaveHelperProtocol.TryParse(line, out var parsed));
            Assert.Equal(status, parsed.Status);
            Assert.Equal(2, parsed.SavedCount);
            Assert.Equal(1, parsed.NoPathCount);
            Assert.Equal(3, parsed.FailedCount);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0|1|2")]
    [InlineData("0|1|2|3|4")]
    [InlineData("x|1|2|3")]
    [InlineData("0|x|2|3")]
    [InlineData("99|0|0|0")]
    public void Protocol_RejectsMalformedInput(string? line)
    {
        Assert.False(OfficeSaveHelperProtocol.TryParse(line, out _));
    }

    // ---- 启动网关：路径校验 / 无 shell / 受控参数（不启动真实进程） ----

    [Fact]
    public void Launcher_MissingHelperFile_ThrowsFileNotFound_WithoutStartingProcess()
    {
        var nonExistentDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var launcher = new OfficeSaveHelperLauncher(nonExistentDirectory);

        var exception = Assert.Throws<FileNotFoundException>(
            () => launcher.Launch(OfficeApplicationKind.Word, Guid.NewGuid().ToString()));

        Assert.EndsWith("AutoShutdown.OfficeSaveHelper.exe", exception.FileName);
    }

    [Fact]
    public void Launcher_BuildStartInfo_UsesNoShellAndControlledArguments()
    {
        var startInfo = OfficeSaveHelperLauncher.BuildStartInfo(
            "C:\\app\\AutoShutdown.OfficeSaveHelper.exe",
            OfficeApplicationKind.PowerPoint,
            "00000000-0000-0000-0000-000000000001");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal("C:\\app\\AutoShutdown.OfficeSaveHelper.exe", startInfo.FileName);
        Assert.Equal(
            new[] { "--app", "PowerPoint", "--id", "00000000-0000-0000-0000-000000000001" },
            startInfo.ArgumentList.ToArray());
    }

    // ---- 辅助进程参数解析：非法枚举 / 额外参数 / 自由文本拒绝 ----

    [Fact]
    public void HelperArguments_AcceptsControlledEnumAndGuid()
    {
        Assert.True(HelperArguments.TryParse(
            ["--app", "Excel", "--id", "00000000-0000-0000-0000-000000000001"],
            out var application,
            out var correlationId));

        Assert.Equal(OfficeApplicationKind.Excel, application);
        Assert.Equal("00000000-0000-0000-0000-000000000001", correlationId);
    }

    [Theory]
    [InlineData("--app", "Bogus", "--id", "00000000-0000-0000-0000-000000000001")] // 非法枚举
    [InlineData("--app", "PowerPoint", "--id", "not-a-guid")]                      // 非法标识
    [InlineData("--app", "Word", "--id", "00000000-0000-0000-0000-000000000001", "--extra")] // 额外参数
    [InlineData("--app", "Word")]                                                  // 缺参数
    [InlineData("--app", "Word", "--id", "1")]                                     // 自由文本 id
    [InlineData("--app", "Word", "--id", "00000000-0000-0000-0000-000000000001", "C:\\secret.docx")] // 任意路径
    public void HelperArguments_RejectsInvalidInput(params string[] args)
    {
        Assert.False(HelperArguments.TryParse(args, out _, out _));
    }

    // ---- ComOfficeAutomation 编排（替身启动器 + 替身进程） ----

    [Fact]
    public void LaunchSuccess_ProcessExits_ParsesResult()
    {
        var process = new FakeProcess("0|2|1|0", exitImmediately: true);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Excel, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.Success, result.Status);
        Assert.Equal(OfficeApplicationKind.Excel, result.Application);
        Assert.Equal(2, result.SavedCount);
        Assert.Equal(1, result.NoPathCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public void LaunchFailure_ReturnsNotDetected()
    {
        var launcher = new FakeLauncher(exception: new InvalidOperationException("launch boom"));
        var automation = new ComOfficeAutomation(launcher);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.NotDetected, result.Status);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task Timeout_KillsHelperTree_AndPropagatesCancellation()
    {
        var process = new FakeProcess(exitImmediately: false);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        // 在后台线程运行同步 SaveOpenDocuments；轮询循环在 80ms 后因令牌取消而终止。
        var task = Task.Run(() => automation.SaveOpenDocuments(OfficeApplicationKind.Word, cts.Token));

        var exception = await Record.ExceptionAsync(async () => await task);

        Assert.IsType<OperationCanceledException>(exception);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitForExitAfterKillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public void Cancel_PreCancelled_KillsTree_AndPropagates()
    {
        var process = new FakeProcess(exitImmediately: false);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => automation.SaveOpenDocuments(OfficeApplicationKind.Word, cts.Token));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitForExitAfterKillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    // ---- D3 聚焦回归测试：取消清理不覆盖 OCE / 解析前取消竞态 ----

    [Fact]
    public void Cleanup_UnconfirmedExit_ThrowsHelperCleanupFailed_NotSilentlyIgnored()
    {
        // D3：WaitForExitAfterKill 返回 false（无法确认退出）时必须成为明确失败路径，
        // 绝不静默忽略确认结果、绝不伪装成功。
        var process = new FakeProcess(confirmExit: false);

        var exception = Assert.Throws<ComOfficeAutomation.HelperCleanupFailedException>(
            () => ComOfficeAutomation.CleanupAfterCancellation(process));

        Assert.Contains("did not confirm exit", exception.Message);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitForExitAfterKillCount);
    }

    [Fact]
    public void Cleanup_NullProcess_IsNoOp()
    {
        // 未启动辅助进程：无可清理目标，视为完成，不抛异常。
        ComOfficeAutomation.CleanupAfterCancellation(null);
    }

    [Fact]
    public void Cancel_KillTreeThrows_StillPropagatesOce()
    {
        // D3：KillTree 抛异常时，外部取消仍以 OCE 传播，不被转换为 NotDetected 等普通结果。
        // D4：清理失败升级为可识别 OCE 子类 OfficeHelperCleanupFailedException（仍属 OCE），改用 ThrowsAny。
        var process = new FakeProcess(throwOnKillTree: true);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => automation.SaveOpenDocuments(OfficeApplicationKind.Word, cts.Token));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public void Cancel_ConfirmExitThrows_StillPropagatesOce()
    {
        // D3：确认退出抛异常时，外部取消仍以 OCE 传播。
        // D4：清理失败升级为可识别 OCE 子类 OfficeHelperCleanupFailedException（仍属 OCE），改用 ThrowsAny。
        var process = new FakeProcess(throwOnWaitForExitAfterKill: true);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => automation.SaveOpenDocuments(OfficeApplicationKind.Word, cts.Token));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitForExitAfterKillCount);
    }

    [Fact]
    public void HelperExited_ThenCancelled_BeforeParse_StillPropagatesOce()
    {
        // D3：Helper 已退出（WaitForExit 返回 true）、解析 stdout 前取消到达时仍传播 OCE，
        // 绝不返回可能掩盖取消的正常结果；并仍执行取消清理（终止+确认）。
        using var cts = new CancellationTokenSource();
        var process = new FakeProcess("0|1|0|0", exitImmediately: true, cancelSource: cts);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);

        Assert.Throws<OperationCanceledException>(
            () => automation.SaveOpenDocuments(OfficeApplicationKind.Word, cts.Token));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitForExitAfterKillCount);
    }

    [Fact]
    public void InvalidOutput_FallsBackToTimedOut()
    {
        var process = new FakeProcess("not-a-valid-line", exitImmediately: true);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.TimedOut, result.Status);
    }

    [Fact]
    public void EmptyOutput_FallsBackToTimedOut()
    {
        var process = new FakeProcess(string.Empty, exitImmediately: true);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);

        var result = automation.SaveOpenDocuments(OfficeApplicationKind.Word, CancellationToken.None);

        Assert.Equal(OfficeAppStatus.TimedOut, result.Status);
    }

    // ---- D4 聚焦回归：清理失败不被空 catch 丢弃，形成可识别安全失败（fail-closed） ----

    [Theory]
    [InlineData(CleanupFailureMode.UnconfirmedExit)]
    [InlineData(CleanupFailureMode.KillTreeThrows)]
    [InlineData(CleanupFailureMode.ConfirmExitThrows)]
    public async Task HardTimeout_CleanupFailure_ThrowsOfficeHelperCleanupFailed(CleanupFailureMode mode)
    {
        // 内部硬超时 + 清理失败（无法确认退出 / Kill 异常 / 确认退出异常）：
        // SaveOpenDocuments 必须抛可识别安全故障（OCE 子类），绝不被空 catch 丢弃、绝不伪装成功。
        var process = CreateFailingProcess(mode);
        var launcher = new FakeLauncher(process);
        var automation = new ComOfficeAutomation(launcher);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        var task = Task.Run(() => automation.SaveOpenDocuments(OfficeApplicationKind.Word, cts.Token));

        var exception = await Record.ExceptionAsync(async () => await task);

        Assert.IsType<OfficeHelperCleanupFailedException>(exception);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Theory]
    [InlineData(CleanupFailureMode.UnconfirmedExit)]
    [InlineData(CleanupFailureMode.KillTreeThrows)]
    [InlineData(CleanupFailureMode.ConfirmExitThrows)]
    public async Task SaveAll_InternalTimeoutCleanupFailure_PropagatesNotConverted(CleanupFailureMode mode)
    {
        // 内部逐应用硬超时 + 清理失败：OfficeDocumentSaver 必须向上传播安全故障，
        // 绝不降级为 TimedOut/NotDetected 等普通可继续结果。
        var process = CreateFailingProcess(mode);
        var launcher = new FakeLauncher(process);
        var automation = new SingleAppAutomation(new ComOfficeAutomation(launcher));
        var saver = new OfficeDocumentSaver(automation, TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAsync<OfficeHelperCleanupFailedException>(
            () => saver.SaveAllAsync(CancellationToken.None));

        Assert.Equal(1, process.KillCount);
    }

    [Theory]
    [InlineData(CleanupFailureMode.UnconfirmedExit)]
    [InlineData(CleanupFailureMode.KillTreeThrows)]
    [InlineData(CleanupFailureMode.ConfirmExitThrows)]
    public async Task SaveAll_ExternalCancelCleanupFailure_PropagatesOce(CleanupFailureMode mode)
    {
        // 外部取消 + 清理失败：仍以 OCE 传播，绝不转 NotDetected（D4 要求 2）；且清理已被尝试。
        var process = CreateFailingProcess(mode);
        var launcher = new FakeLauncher(process);
        var automation = new SingleAppAutomation(new ComOfficeAutomation(launcher));
        var saver = new OfficeDocumentSaver(automation, TimeSpan.FromMilliseconds(1000));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => saver.SaveAllAsync(cts.Token));

        Assert.Equal(1, process.KillCount);
    }

    [Theory]
    [InlineData(CleanupFailureMode.UnconfirmedExit)]
    [InlineData(CleanupFailureMode.KillTreeThrows)]
    [InlineData(CleanupFailureMode.ConfirmExitThrows)]
    public async Task EndToEnd_CleanupFailure_PropagatesOce_AndStopsWorkflow(CleanupFailureMode mode)
    {
        // 端到端（SaveOpenDocuments → OfficeDocumentSaver → OfficeSaveAction → PrePipelineRunner）：
        // 内部硬超时 + 清理失败必须作为 OCE 子类（OfficeHelperCleanupFailedException）原样传播，
        // 终止工作流（Runner 不返回结果 → 唯一电源出口不触发），绝不作为普通 Continue 结果继续。
        var process = CreateFailingProcess(mode);
        var launcher = new FakeLauncher(process);
        var automation = new SingleAppAutomation(new ComOfficeAutomation(launcher));
        var action = new OfficeSaveAction(automation, perAppTimeout: TimeSpan.FromMilliseconds(80));
        var runner = new PrePipelineRunner([action]);

        await Assert.ThrowsAsync<OfficeHelperCleanupFailedException>(
            () => runner.RunAsync(Context(), CancellationToken.None));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    // ---- D5 聚焦回归：安全故障作为 OCE 传播（无跨任务可变状态污染） ----

    [Fact]
    public async Task SaveAll_NormalTimeout_CleanupSucceeds_IsTimedOut()
    {
        // 普通内部硬超时且 Helper 成功终止：仍为 TimedOut（可继续），绝不升级为安全故障。
        var process = new FakeProcess(exitImmediately: false); // 清理成功（confirmExit 默认 true）
        var launcher = new FakeLauncher(process);
        var automation = new SingleAppAutomation(new ComOfficeAutomation(launcher));
        var saver = new OfficeDocumentSaver(automation, TimeSpan.FromMilliseconds(80));

        var report = await saver.SaveAllAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal("Word: timed out", report.Summary);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitForExitAfterKillCount);
    }

    [Fact]
    public async Task EndToEnd_OrdinaryFailure_ContinuesPipeline()
    {
        // 普通 Office 保存失败（非清理安全故障）→ 默认 Continue：Runner 走完（Completed），不 Block。
        var launcher = new FakeLauncher(exception: new InvalidOperationException("boom"));
        var automation = new SingleAppAutomation(new ComOfficeAutomation(launcher));
        var action = new OfficeSaveAction(automation, perAppTimeout: TimeSpan.FromMilliseconds(80));
        var runner = new PrePipelineRunner([action]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Completed, result.Status);
        Assert.True(result.PowerAllowed);
        var recorded = Assert.Single(result.Actions);
        Assert.False(recorded.Succeeded);
        Assert.Equal(FailurePolicy.Continue, recorded.FailurePolicy);
    }

    [Fact]
    public async Task Action_CleanupFault_ThenNormalRun_IsNotPolluted()
    {
        // 同一 OfficeSaveAction：第一次清理安全故障后，第二次正常执行不受前次状态污染；
        // FailurePolicy 始终保持不可变 Continue（无共享可变字段）。
        var action = new OfficeSaveAction(new FailOnceThenSucceedAutomation());

        await Assert.ThrowsAsync<OfficeHelperCleanupFailedException>(
            () => action.ExecuteAsync(Context(), CancellationToken.None));

        Assert.Equal(FailurePolicy.Continue, action.FailurePolicy);

        var second = await action.ExecuteAsync(Context(), CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(FailurePolicy.Continue, action.FailurePolicy);
    }

    [Fact]
    public async Task Action_Concurrent_FaultAndNormal_NoInterference()
    {
        // 同一 Action 并发执行：一个清理安全故障、一个正常完成，两者互不影响（无共享可变状态）。
        var action = new OfficeSaveAction(new FailOnceThenSucceedAutomation());

        var task1 = action.ExecuteAsync(Context(), CancellationToken.None);
        var task2 = action.ExecuteAsync(Context(), CancellationToken.None);

        var outcome1 = await Record.ExceptionAsync(async () => await task1);
        var outcome2 = await Record.ExceptionAsync(async () => await task2);

        Assert.Equal(
            1,
            new[] { outcome1, outcome2 }.Count(exception => exception is OfficeHelperCleanupFailedException));
        Assert.Equal(
            1,
            new[] { outcome1, outcome2 }.Count(exception => exception is null));
        Assert.Equal(FailurePolicy.Continue, action.FailurePolicy);
    }

    // ---- 结构契约 ----

    [Fact]
    public void AppSources_ProcessStartOnlyInOfficeSaveHelperLauncher()
    {
        foreach (var file in EnumerateProjectSources("AutoShutdown.App"))
        {
            var content = File.ReadAllText(file);
            if (Path.GetFileName(file) == "OfficeSaveHelperLauncher.cs")
            {
                Assert.Contains("Process.Start", content);
            }
            else
            {
                Assert.DoesNotContain("Process.Start", content);
            }
        }
    }

    [Fact]
    public void HelperSources_HaveNoProcessStartOrDllImportOrActivator()
    {
        foreach (var file in EnumerateProjectSources("AutoShutdown.OfficeSaveHelper"))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("Process.Start", content);
            Assert.DoesNotContain("DllImport", content);
            Assert.DoesNotContain("Activator.CreateInstance", content);
        }
    }

    [Fact]
    public void HelperGatewayTypes_ExposeNoQuitOrCreateMethod()
    {
        // Office Helper 不 Quit / 不创建：公开 COM 契约仅暴露附加与保存，无 Quit/Create 入口。
        AssertNoMethodNamed(typeof(IOfficeComGateway), "Quit", "Create", "CreateInstance");
        AssertNoMethodNamed(typeof(IOfficeComApplication), "Quit", "Create", "CreateInstance");
        AssertNoMethodNamed(typeof(IOfficeComDocument), "Quit", "Create", "CreateInstance", "SaveAs");
    }

    // ---- Helpers ----

    private static void AssertNoMethodNamed(Type type, params string[] names)
    {
        foreach (var method in type.GetMethods())
        {
            Assert.DoesNotContain(method.Name, names);
        }
    }

    private static IEnumerable<string> EnumerateProjectSources(string projectName)
    {
        var root = FindSourceRoot(projectName);
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindSourceRoot(string projectName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", projectName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"The {projectName} source directory was not found.");
    }

    public enum CleanupFailureMode
    {
        UnconfirmedExit,
        KillTreeThrows,
        ConfirmExitThrows
    }

    private static FakeProcess CreateFailingProcess(CleanupFailureMode mode) => mode switch
    {
        CleanupFailureMode.UnconfirmedExit => new FakeProcess(exitImmediately: false, confirmExit: false),
        CleanupFailureMode.KillTreeThrows => new FakeProcess(exitImmediately: false, throwOnKillTree: true),
        CleanupFailureMode.ConfirmExitThrows => new FakeProcess(exitImmediately: false, throwOnWaitForExitAfterKill: true),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static PrePipelineContext Context() => new()
    {
        InstanceId = Guid.NewGuid(),
        SourceTaskId = Guid.NewGuid(),
        Action = PowerAction.Shutdown,
        ScheduledFireTime = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// 强制探测到单个 Word 应用、保存委托给真实 <see cref="ComOfficeAutomation"/> 的替身，
    /// 用于端到端测试（真实编排路径 + 替身启动器/进程，不触碰真实 Office/COM）。
    /// </summary>
    private sealed class SingleAppAutomation : IOfficeAutomation
    {
        private readonly IOfficeAutomation _inner;

        public SingleAppAutomation(IOfficeAutomation inner) => _inner = inner;

        public IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications()
            => [OfficeApplicationKind.Word];

        public OfficeApplicationSaveResult SaveOpenDocuments(
            OfficeApplicationKind application,
            CancellationToken cancellationToken)
            => _inner.SaveOpenDocuments(application, cancellationToken);
    }

    /// <summary>
    /// 第一次 SaveOpenDocuments 抛清理安全故障、之后正常成功的替身，用于验证 OfficeSaveAction
    /// 无跨调用可变状态（D5：状态污染 / 并发互不影响均不得发生）。
    /// </summary>
    private sealed class FailOnceThenSucceedAutomation : IOfficeAutomation
    {
        private int _saveCalls;

        public IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications()
            => [OfficeApplicationKind.Word];

        public OfficeApplicationSaveResult SaveOpenDocuments(
            OfficeApplicationKind application,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _saveCalls) == 1)
            {
                throw new OfficeHelperCleanupFailedException("cleanup fault");
            }

            return new OfficeApplicationSaveResult
            {
                Application = application,
                Status = OfficeAppStatus.Success,
                SavedCount = 1
            };
        }
    }

    private sealed class FakeLauncher : IOfficeSaveHelperLauncher
    {
        private readonly IOfficeSaveHelperProcess? _process;
        private readonly Exception? _exception;

        public FakeLauncher(IOfficeSaveHelperProcess? process = null, Exception? exception = null)
        {
            _process = process;
            _exception = exception;
        }

        public int LaunchCount { get; private set; }

        public IOfficeSaveHelperProcess Launch(OfficeApplicationKind application, string correlationId)
        {
            LaunchCount++;
            if (_exception is not null)
            {
                throw _exception;
            }

            return _process ?? throw new InvalidOperationException("no process configured");
        }
    }

    private sealed class FakeProcess : IOfficeSaveHelperProcess
    {
        private readonly string _stdout;
        private readonly bool _throwOnKillTree;
        private readonly bool _throwOnWaitForExitAfterKill;
        private readonly bool _confirmExit;
        private readonly CancellationTokenSource? _cancelSource;

        public FakeProcess(
            string stdout = "",
            bool exitImmediately = false,
            bool throwOnKillTree = false,
            bool throwOnWaitForExitAfterKill = false,
            bool confirmExit = true,
            CancellationTokenSource? cancelSource = null)
        {
            _stdout = stdout;
            _throwOnKillTree = throwOnKillTree;
            _throwOnWaitForExitAfterKill = throwOnWaitForExitAfterKill;
            _confirmExit = confirmExit;
            _cancelSource = cancelSource;
            Exited = exitImmediately;
        }

        public bool Exited { get; private set; }

        public int KillCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int WaitForExitAfterKillCount { get; private set; }

        public bool HasExited => Exited;

        public bool WaitForExit(int milliseconds)
        {
            Thread.Sleep(5); // 模拟真实轮询等待
            // 模拟「Helper 已退出、解析前取消到达」的竞态：退出瞬间取消令牌。
            _cancelSource?.Cancel();
            return Exited;
        }

        public string ReadStandardOutput() => _stdout;

        public void KillTree()
        {
            KillCount++;
            if (_throwOnKillTree)
            {
                throw new InvalidOperationException("kill boom");
            }

            Exited = true;
        }

        public bool WaitForExitAfterKill(TimeSpan timeout)
        {
            WaitForExitAfterKillCount++;
            if (_throwOnWaitForExitAfterKill)
            {
                throw new InvalidOperationException("wait boom");
            }

            Exited = true;
            return _confirmExit;
        }

        public void Dispose() => DisposeCount++;
    }
}
