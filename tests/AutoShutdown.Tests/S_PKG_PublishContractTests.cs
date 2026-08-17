using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG 发布工具契约测试（静态文本检查，只读）。
/// 验证统一候选构建入口 tools/Publish-ReleaseCandidate.ps1：
///  - 不含真实电源命令 / 注册表 / 自启 / 防火墙 / 任务计划操作；
///  - 候选命名与 SHA-256 生成逻辑存在；
///  - 路径安全（Assert-AllowedPath）生效；
///  - 候选 manifest 显式标注签名状态。
/// 绝不调用发布脚本、不碰注册表、不启动应用。
/// </summary>
public sealed class S_PKG_PublishContractTests
{
    private static readonly string Script =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "Publish-ReleaseCandidate.ps1"));

    private static readonly string Lib =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "SPkg-Lib.ps1"));

    // ---- 1. 发布脚本不含真实电源命令 ----

    [Fact]
    public void PublishCandidate_HasNoRealPowerCommands()
    {
        Assert.DoesNotContain("Win32PowerService", Script);
        Assert.DoesNotContain("shutdown.exe", Script);
        Assert.DoesNotContain("ExitWindowsEx", Script);
        Assert.DoesNotContain("SetSuspendState", Script);
        Assert.DoesNotContain("IPowerService", Script);
    }

    // ---- 2. 发布脚本不写注册表、不启用自启动 ----

    [Fact]
    public void PublishCandidate_DoesNotWriteRegistryOrEnableAutoStart()
    {
        Assert.DoesNotContain("Registry.CurrentUser", Script);
        Assert.DoesNotContain("Microsoft.Win32", Script);
        Assert.DoesNotContain("CurrentVersion\\Run", Script);
        Assert.DoesNotContain("StartWithWindows", Script);
        Assert.DoesNotContain("AutoStartService", Script);
    }

    // ---- 3. 发布脚本不操作防火墙 / 任务计划程序 ----

    [Fact]
    public void PublishCandidate_DoesNotTouchFirewallOrTaskScheduler()
    {
        Assert.DoesNotContain("New-NetFirewallRule", Script);
        Assert.DoesNotContain("netsh advfirewall", Script);
        Assert.DoesNotContain("Register-ScheduledTask", Script);
        Assert.DoesNotContain("schtasks", Script);
        Assert.DoesNotContain("TaskScheduler", Script);
    }

    // ---- 4. 发布参数明确关闭 Trim / ReadyToRun；单文件候选与轻量包并存 ----

    [Fact]
    public void PublishCandidate_ParametersDisableTrimAndReadyToRun()
    {
        Assert.Contains("PublishTrimmed=false", Script);
        Assert.Contains("PublishReadyToRun=false", Script);
        Assert.Contains("PublishSingleFile=true", Script);
        Assert.Contains("DebugType=None", Script);
        Assert.Contains("DebugSymbols=false", Script);
        Assert.Contains("win-x64-framework-dependent", Script);
    }

    // ---- 5. 候选命名与 SHA-256 生成 ----

    [Fact]
    public void PublishCandidate_NamingAndSha256ArePresent()
    {
        // S-PKG 候选命名决策：AutoShutdown-v{MAJOR}.{MINOR}.{PATCH}-S{STEP}.{BUILD}
        Assert.Contains("AutoShutdown-v$Version-$Step.$Build", Script);
        Assert.Contains("Get-Sha256Hex", Script);
        Assert.Contains("Get-FileHash -Algorithm SHA256", Lib);
        Assert.Contains("sha256", Script);
        Assert.Contains("source-commit", Script);
    }

    // ---- 6. 签名状态必须显式标注（未签名候选，绝不伪称已签名） ----

    [Fact]
    public void PublishCandidate_MarksSigningStatusExplicitly()
    {
        Assert.Contains("signing-status", Script);
        Assert.Contains("unsigned-candidate", Script);
        Assert.Contains("未签名候选", Script);
        Assert.DoesNotContain("Authenticode", Script);
        Assert.DoesNotContain("signtool", Script);
    }

    // ---- 7. 路径安全 ----

    [Fact]
    public void PublishCandidate_AssertAllowedPathIsUsed()
    {
        Assert.Contains("Assert-AllowedPath", Script);
        Assert.Contains("路径超出允许范围", Lib);
        Assert.Contains("GetFullPath", Lib);
    }

    // ---- 8. 库文件不包含破坏性命令 ----

    [Fact]
    public void SPkgLib_HasNoDestructiveSystemCommands()
    {
        Assert.DoesNotContain("Remove-Item -Recurse -Force", Lib);
        Assert.DoesNotContain("New-NetFirewallRule", Lib);
        Assert.DoesNotContain("Register-ScheduledTask", Lib);
        Assert.DoesNotContain("shutdown", Lib);
        Assert.DoesNotContain("Set-Service", Lib);
    }

    private static string FindToolsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tools");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The tools directory was not found.");
    }
}
