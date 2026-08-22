using System.Xml.Linq;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class S_UI3_UiTestLauncherTests
{
    [Fact]
    public void AppProject_LinksSharedIconIntoWpfResourcePath()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "AutoShutdown.App", "AutoShutdown.App.csproj"));

        // 图标资源仅一条 Resource 条目：Link 归一化项目内路径 + LogicalName 固定清单名，
        // 不会产生重复资源、重复输出或路径冲突（同一份 ICO 供主窗口/提醒窗口/托盘/EXE 图标共用）。
        Assert.Equal(1, CountOccurrences(project, "<Resource Include=\"..\\..\\assets\\icon.ico\">"));
        Assert.Contains("<Resource Include=\"..\\..\\assets\\icon.ico\">", project, StringComparison.Ordinal);
        Assert.Contains("<Link>assets\\icon.ico</Link>", project, StringComparison.Ordinal);
        Assert.Contains("<LogicalName>assets/icon.ico</LogicalName>", project, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Cmd_OnlyDelegatesToControlledPowerShellLauncher()
    {
        var text = File.ReadAllText(Path.Combine(Root, "启动AutoShutdown安全测试界面.cmd"));
        Assert.Contains("tools\\Start-AutoShutdownUiTest.ps1", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AutoShutdown.App.exe", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launcher_UsesCurrentHead_ExactBuildOutput_AndFailClosedPreflight()
    {
        var text = Launcher();
        Assert.Contains("rev-parse HEAD", text);
        Assert.Contains("dotnet build", text);
        Assert.Contains("AutoShutdown.App.exe", text);
        Assert.Contains("Release 构建失败，不会启动任何旧程序", text);
        Assert.Contains("UiTestSandbox", text);
        Assert.Contains("TestMode", text);
        Assert.Contains("RealPowerEnabled", text);
        Assert.Contains("StartWithWindows", text);
        Assert.Contains("RunCommands", text);
        Assert.Contains("CloseApps", text);
        Assert.Contains("unattended.json", text);
        Assert.Contains("task-sync.json", text);
        Assert.Contains("remote-settings.json", text);
    }

    [Fact]
    public void Launcher_HasNoDownload_Elevation_ProcessKill_OrRepositoryPublication()
    {
        var text = Launcher() + File.ReadAllText(Path.Combine(Root, "tools", "test", "Invoke-ASUI3Smoke.ps1"));
        foreach (var forbidden in new[] { "Invoke-WebRequest", "WebClient", "HttpClient", "Start-BitsTransfer",
                     "-Verb RunAs", "taskkill", "Stop-Process", "Kill(", "git push", "Register-ScheduledTask" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Smoke_HasPerRoundIsolatedDataRoot_AndDoesNotReuseSharedSandbox()
    {
        var smoke = Smoke();
        // A：每轮独立精确数据根（as-ui3-round-N-<guid>），绝不复用共享 UiTestSandbox。
        Assert.Contains("as-ui3-round-", smoke, StringComparison.Ordinal);
        Assert.Contains("$env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot", smoke, StringComparison.Ordinal);
        Assert.Contains("New-IsolatedRoot", smoke, StringComparison.Ordinal);
        Assert.Contains("Remove-IsolatedRoot", smoke, StringComparison.Ordinal);
        // 冒烟不得把数据根指向共享沙箱（共享沙箱只在启动器中由正式 UI 测试使用）。
        Assert.DoesNotContain("$env:AUTOSHUTDOWN_DATA_ROOT = $sandbox", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_UsesBoundedConditionalPolling_NotFixedLongSleeps()
    {
        var smoke = Smoke();
        // B：有界条件轮询（显式总超时 + 300ms 间隔），替代固定 1200/1000/600ms 盲等。
        Assert.Contains("Wait-ButtonCount", smoke, StringComparison.Ordinal);
        Assert.Contains("Wait-NoIdleState", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep -Milliseconds 1200", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep -Milliseconds 1000", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep -Milliseconds 600", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_VerifiesCloseToTray_ThenRealTrayExitByExactPid()
    {
        var smoke = Smoke();
        // C1：窗口关闭 → 主窗口隐藏 + 进程仍存活（不把“关闭到托盘”误判为“退出失败”）。
        Assert.Contains("Close-WindowGracefully", smoke, StringComparison.Ordinal);
        Assert.Contains("MainWindowHandle -eq 0", smoke, StringComparison.Ordinal);
        // C2：真实托盘「退出程序」按本轮精确 PID（不按进程名清理、不强杀）。
        Assert.Contains("-TargetPid $proc.Id", smoke, StringComparison.Ordinal);
        Assert.Contains("TrayExitRequested", smoke, StringComparison.Ordinal);
        Assert.Contains("ApplicationStopping", smoke, StringComparison.Ordinal);
        Assert.Contains("ApplicationStopped", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_WritesPerRoundEvidence_AndGuardsTempDeletion()
    {
        var smoke = Smoke();
        // D：每轮完整控制台输出落盘 + app 日志保留；正式数据目录前后快照一致。
        Assert.Contains("ui3-round-{0}-console.log", smoke, StringComparison.Ordinal);
        Assert.Contains("ui3-round-{0}-applogs", smoke, StringComparison.Ordinal);
        Assert.Contains("Get-FormalDataSnapshot", smoke, StringComparison.Ordinal);
        // 清理仅限系统临时目录内、且本脚本创建的精确路径（无无界递归删除、无 Bash/rm）。
        Assert.Contains("[IO.Path]::GetTempPath()", smoke, StringComparison.Ordinal);
        Assert.Contains(".StartsWith($tempFull", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void App_RefusesUnsafeUiTestConfig_AndDoesNotForwardSecondaryInstance()
    {
        var guard = File.ReadAllText(Path.Combine(Root, "src", "AutoShutdown.App", "Infrastructure", "UiTestEnvironment.cs"));
        var app = File.ReadAllText(Path.Combine(Root, "src", "AutoShutdown.App", "App.xaml.cs"));
        Assert.Contains("TryValidate", guard);
        Assert.Contains("TestMode", guard);
        Assert.Contains("RunCommands", guard);
        Assert.Contains("CloseApps", guard);
        Assert.Contains("UiTestExistingInstanceRefused", app);
        Assert.True(app.IndexOf("UiTestExistingInstanceRefused", StringComparison.Ordinal)
                    < app.IndexOf("ActivationPipeClient.Try", StringComparison.Ordinal));
    }

    [Fact]
    public void Ui_ShowsExactSafetyBanner_AndReadOnlyEnvironmentCard()
    {
        var xamlPath = Path.Combine(Root, "src", "AutoShutdown.App", "MainWindow.xaml");
        _ = XDocument.Load(xamlPath);
        var xaml = File.ReadAllText(xamlPath);
        var vm = File.ReadAllText(Path.Combine(Root, "src", "AutoShutdown.App", "Presentation", "MainWindowViewModel.cs"));
        Assert.Contains("当前测试环境", xaml);
        Assert.Contains("打开测试数据目录", xaml);
        Assert.Contains("复制测试环境摘要", xaml);
        Assert.Contains("截图当前窗口", xaml);
        Assert.Contains("安全测试模式——不会执行真实系统电源操作", vm);
        Assert.Contains("TestEnvironmentCommitText", xaml);
        Assert.Contains("TestEnvironmentTaskCountText", xaml);
    }

    [Fact]
    public void ProductionPowerOutlet_RemainsOnlyWorkflowPath()
    {
        var production = Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(item => item.Text.Contains("IPowerService", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(production, item => item.Path.EndsWith("UiTestEnvironment.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("IPowerService", Launcher(), StringComparison.Ordinal);
    }

    private static string Launcher() => File.ReadAllText(Path.Combine(Root, "tools", "Start-AutoShutdownUiTest.ps1"));

    private static string Smoke() => File.ReadAllText(Path.Combine(Root, "tools", "test", "Invoke-ASUI3Smoke.ps1"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AutoShutdown.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
