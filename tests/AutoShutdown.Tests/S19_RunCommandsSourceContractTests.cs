using System.IO;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19 C4 源码契约测试。验证 RunCommands 的进程启动边界与安全不变量：
/// 1) CommandRunner 用 ProcessStartInfo.ArgumentList（逐字面量，无 shell 字符串拼接）、
///    UseShellExecute=false、CreateNoWindow=true，超时/取消终止整棵进程树
///    （entireProcessTree），限量输出与有界退出确认；绝不引入 P/Invoke、Process.Start(string)、
///    cmd/powershell/taskkill/shutdown 等逃逸。
/// 2) RunCommands 全部 Core 类型与 ICommandRunner 抽象不触碰 IPowerService（唯一电源出口保持）。
/// 3) 本地白名单为不可变 record，无任何远程/网络旁路接口（与 S23 远程层隔离）。
/// </summary>
public sealed class S19_RunCommandsSourceContractTests
{
    [Fact]
    public void CommandRunner_UsesArgumentList_NoShellStringConcatenation_AndKillsEntireTree()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceRoot(), "Infrastructure", "Commands", "CommandRunner.cs"));

        // 无 shell 注入：逐字面量参数列表，绝不设置 string 版 Arguments。
        Assert.Contains("ArgumentList", source);
        Assert.DoesNotContain("startInfo.Arguments", source);
        Assert.Contains("UseShellExecute = false", source);
        Assert.Contains("CreateNoWindow = true", source);

        // 超时/取消：终止整棵进程树（托管，无 P/Invoke）。
        Assert.Contains("entireProcessTree", source);

        // 无 P/Invoke、无 shell 逃逸、无电源/系统关机逃逸、无静态 Process.Start(string)。
        Assert.DoesNotContain("DllImport", source);
        Assert.DoesNotContain("Process.Start", source);
        Assert.DoesNotContain("cmd.exe", source);
        Assert.DoesNotContain("powershell", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shutdown.exe", source);
        Assert.DoesNotContain("ExitWindowsEx", source);
        Assert.DoesNotContain("SetSuspendState", source);
    }

    [Fact]
    public void CommandRunner_BoundsOutput_AndBoundsExitConfirmation()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceRoot(), "Infrastructure", "Commands", "CommandRunner.cs"));

        Assert.Contains("MaxOutputCharsPerStream", source); // 限量输出
        Assert.Contains("KillConfirmTimeoutMs", source);   // 有界退出确认
    }

    [Fact]
    public void RunCommandsCoreTypes_AndAbstraction_HaveNoPowerServiceDependency()
    {
        var files = CoreRunCommandsSources()
            .Append(Path.Combine(CoreSourceRoot(), "Abstractions", "ICommandRunner.cs"));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("IPowerService", content);
            Assert.DoesNotContain("PowerRequest", content);
            Assert.DoesNotContain("PowerResult", content);
            Assert.DoesNotContain("ExitWindowsEx", content);
            Assert.DoesNotContain("DllImport", content);
            Assert.DoesNotContain("shutdown.exe", content);
        }
    }

    [Fact]
    public void LocalWhitelist_IsImmutableRecord_WithNoRemoteOrBypassSurface()
    {
        var whitelist = File.ReadAllText(Path.Combine(CoreSourceRoot(), "RunCommands", "LocalCommandWhitelist.cs"));
        var authorizer = File.ReadAllText(Path.Combine(CoreSourceRoot(), "RunCommands", "CommandWhitelist.cs"));

        Assert.Contains("record LocalCommandWhitelist", whitelist);
        Assert.Contains("record WhitelistEntry", whitelist);
        Assert.Contains("init;", whitelist); // 不可变 init-only 属性

        Assert.DoesNotContain("HttpClient", whitelist);
        Assert.DoesNotContain("Socket", whitelist);
        Assert.DoesNotContain("NetworkStream", whitelist);
        Assert.DoesNotContain("HttpClient", authorizer);
        Assert.DoesNotContain("Socket", authorizer);
        Assert.DoesNotContain("NetworkStream", authorizer);
    }

    private static IEnumerable<string> CoreRunCommandsSources()
        => Directory.GetFiles(Path.Combine(CoreSourceRoot(), "RunCommands"), "*.cs");

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
