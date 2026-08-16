using System.Diagnostics;
using AutoShutdown.App.Infrastructure.Office;
using AutoShutdown.Core.Office;
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
        Assert.Equal(1, process.DisposeCount);
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

        public FakeProcess(string stdout = "", bool exitImmediately = false)
        {
            _stdout = stdout;
            Exited = exitImmediately;
        }

        public bool Exited { get; private set; }

        public int KillCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool HasExited => Exited;

        public bool WaitForExit(int milliseconds)
        {
            Thread.Sleep(5); // 模拟真实轮询等待
            return Exited;
        }

        public string ReadStandardOutput() => _stdout;

        public void KillTree()
        {
            KillCount++;
            Exited = true;
        }

        public bool WaitForExitAfterKill(TimeSpan timeout)
        {
            Exited = true;
            return true;
        }

        public void Dispose() => DisposeCount++;
    }
}
