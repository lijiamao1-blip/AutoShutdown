using System.Xml.Linq;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class S_UI3_UiTestLauncherTests
{
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
