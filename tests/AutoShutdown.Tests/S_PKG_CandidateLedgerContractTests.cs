using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG 候选制品账本生成器契约测试（静态文本检查，只读）。
/// 验证 tools/New-CandidateLedger.ps1：
///  - 纯只读扫描 + 仅写 artifacts/release/&lt;version&gt;/manifests/：不含真实电源 /
///    注册表 / 防火墙 / 任务计划 / 安装卸载命令；
///  - 每个条目都计算 SHA-256 并核对 Git 提交（commitExists），候选名 token 按
///    [.-] 切分以正确解析 S13/S23/PKG 形如 v2.0.0-S23.97cef24 的名字；
///  - S16/S20 阶段如实登记为 no-standalone-artifact，绝不伪造候选；
///  - 签名状态统一标记 unsigned-candidate，绝不伪称已签名。
/// 不执行生成器、不写任何目录、不触碰真实系统。
/// </summary>
public sealed class S_PKG_CandidateLedgerContractTests
{
    private static readonly string Generator =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "New-CandidateLedger.ps1"));

    // ---- 1. 生成器是只读扫描 + 受限写 ----

    [Fact]
    public void Generator_NoRealPowerOrSystemCommands()
    {
        Assert.DoesNotContain("shutdown.exe", Generator);
        Assert.DoesNotContain("Stop-Computer", Generator);
        Assert.DoesNotContain("Restart-Computer", Generator);
        Assert.DoesNotContain("New-NetFirewallRule", Generator);
        Assert.DoesNotContain("Register-ScheduledTask", Generator);
        Assert.DoesNotContain("schtasks", Generator);
        Assert.DoesNotContain("Registry.CurrentUser", Generator);
        Assert.DoesNotContain("Invoke-ASUninstall", Generator);
    }

    [Fact]
    public void Generator_WritesOnlyToManifestsDir()
    {
        // 唯一的写路径落在 manifests 目录内；其余为只读扫描（Get-ChildItem / Get-FileHash）。
        Assert.Contains("Join-Path $OutRoot 'manifests'", Generator);
        Assert.Contains("Set-Content -LiteralPath $txtPath", Generator);
    }

    // ---- 2. 每个条目都计算 SHA-256 并核对 Git 提交 ----

    [Fact]
    public void Generator_HashesAndChecksGitCommitPerEntry()
    {
        Assert.Contains("Get-FileHash -Algorithm SHA256", Generator);
        Assert.Contains("Test-GitCommit", Generator);
        Assert.Contains("commitExists = Test-GitCommit $buildToken", Generator);
    }

    [Fact]
    public void Generator_ParsesCandidateNameTokensCorrectly()
    {
        // v2.0.0-S23.97cef24 -> [2,0,0,S23,97cef24]：必须按 [.-] 切分，build 取末 token。
        Assert.Contains("-split '[.-]'", Generator);
        Assert.Contains("$buildToken", Generator);
    }

    // ---- 3. 诚实登记：无独立候选的阶段绝不伪造 ----

    [Fact]
    public void Generator_HonestlyRecordsNoStandaloneArtifact()
    {
        Assert.Contains("'S16'", Generator);
        Assert.Contains("'S20'", Generator);
        Assert.Contains("no-standalone-artifact", Generator);
    }

    [Fact]
    public void Generator_NeverClaimsSignedStatus()
    {
        // 签名证书不可用时只标记未签名候选，绝不写 signing-status= signed。
        Assert.Contains("'unsigned-candidate'", Generator);
        Assert.DoesNotContain("signed-release", Generator);
        Assert.DoesNotContain("'signed'", Generator);
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
