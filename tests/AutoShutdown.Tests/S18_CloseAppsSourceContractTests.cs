using System.IO;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18 C1 源码契约测试。验证 CloseApps 的 Win32/进程网关边界：
/// 1) 真实网关（DiagnosticProcessManager / DiagnosticAppWindowManager）不引入任何
///    P/Invoke、Process.Start、cmd/PowerShell/shell，只用托管 System.Diagnostics.Process；
/// 2) 优雅关闭优先（CloseMainWindow = WM_CLOSE），强杀前复核 PID + 启动时间；
/// 3) CloseApps 全部类型/动作不触碰 IPowerService（唯一电源出口保持）。
/// </summary>
public sealed class S18_CloseAppsSourceContractTests
{
    [Fact]
    public void DiagnosticGateways_HaveNoNativeOrShellEscape()
    {
        foreach (var file in CloseAppsAppSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("DllImport", content);
            Assert.DoesNotContain("Process.Start", content);
            Assert.DoesNotContain("shutdown.exe", content);
            Assert.DoesNotContain("cmd.exe", content);
            Assert.DoesNotContain("powershell", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("taskkill", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExitWindowsEx", content);
            Assert.DoesNotContain("SetSuspendState", content);
        }
    }

    [Fact]
    public void DiagnosticAppWindowManager_IsGracefulFirst_AndReverifiesPidBeforeKill()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceRoot(), "Infrastructure", "CloseApps", "DiagnosticAppWindowManager.cs"));

        // 优雅关闭：托管 CloseMainWindow（等价 WM_CLOSE）。
        Assert.Contains("CloseMainWindow", source);
        // 强杀前复核 PID + 启动时间。
        Assert.Contains("expectedStartTimeUtc", source);
        Assert.Contains("StartTime", source);
        Assert.Contains("PidReuseDetected", source);
        // 强杀只在 ForceKill 内（Kill 出现在强杀分支，且访问拒绝被安全收敛为 AccessDenied）。
        Assert.Contains("ForceKill", source);
        Assert.Contains("AccessDenied", source);
    }

    [Fact]
    public void CloseAppsTypesAndAction_HaveNoPowerServiceDependency()
    {
        foreach (var file in CloseAppsCoreSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("IPowerService", content);
            Assert.DoesNotContain("PowerRequest", content);
            Assert.DoesNotContain("PowerResult", content);
            Assert.DoesNotContain("ExitWindowsEx", content);
        }
    }

    [Fact]
    public void CloseAppsAbstractions_ExposeNoPowerAndNoRawKillSurface()
    {
        var processManager = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Abstractions", "IProcessManager.cs"));
        var windowManager = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Abstractions", "IAppWindowManager.cs"));

        Assert.DoesNotContain("IPowerService", processManager);
        Assert.DoesNotContain("IPowerService", windowManager);
        // 强杀通过返回 ForceKillResult 的显式边界暴露，而非暴露原始 Kill。
        Assert.Contains("ForceKillResult ForceKill", windowManager);
    }

    private static IEnumerable<string> CloseAppsAppSources()
    {
        var dir = Path.Combine(AppSourceRoot(), "Infrastructure", "CloseApps");
        return Directory.GetFiles(dir, "*.cs");
    }

    private static IEnumerable<string> CloseAppsCoreSources()
    {
        var dir = Path.Combine(CoreSourceRoot(), "CloseApps");
        return Directory.GetFiles(dir, "*.cs");
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
