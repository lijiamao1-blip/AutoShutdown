using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG D4 候选树子 junction 越界防护契约测试（静态文本检查，只读）。
/// 验证 tools/SPkg-Lifecycle.ps1：
///  - 存在候选整树预检 Test-ASCandidateTreeSafe / Assert-ASCandidateTreeSafe：
///    复用 Get-ASDirTreeSafe（base 全祖先链 + base 自身 + 整树子节点 reparse 探测）与
///    全祖先链守卫 Test-ASFullChainSafe；候选根、任意子目录或文件含
///    junction/symlink/reparse point 即拒绝。
///  - 预检接入 Replace-ASBinary 与 Invoke-ASReinstall，且位于任何备份槽创建、
///    删除旧 appFiles、删除 owner marker、创建安装目录或复制候选文件之前。
///  - D4 装置 Invoke-SPD4CandidateTreeTests.ps1 用「候选根正常 + 候选子目录 junction
///    -> outside」覆盖 reinstall 与 replace：断言操作拒绝、outside 哨兵 SHA-256 不变、
///    旧 EXE SHA-256 不变、owner marker 内容不变、用户文件不变、无半安装/无备份残留；
///    候选顶层文件 symlink 环境不支持时明确标「环境跳过」。
/// 绝不调用生命周期脚本、不写任何目录、不触碰真实系统。
/// </summary>
public sealed class S_PKG_D4_CandidateTreeContractTests
{
    private static readonly string Lifecycle =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "SPkg-Lifecycle.ps1"));

    private static readonly string Harness =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPD4CandidateTreeTests.ps1"));

    // ---- 1. 候选整树预检函数存在且复用现有原语 ----

    [Fact]
    public void D4_CandidateTreePreCheck_Present()
    {
        Assert.Contains("function Test-ASCandidateTreeSafe", Lifecycle);
        Assert.Contains("function Assert-ASCandidateTreeSafe", Lifecycle);
        // 复用现有 Get-ASDirTreeSafe（整树不跟随安全枚举）与全祖先链守卫。
        Assert.Contains("$chain = Test-ASFullChainSafe -Path $candFull", Lifecycle);
        Assert.Contains("$tree = Get-ASDirTreeSafe -BaseDir $candFull", Lifecycle);
    }

    // ---- 2. 候选根、任意子目录或文件含 reparse 即拒绝（fail-closed） ----

    [Fact]
    public void D4_AnyCandidateNodeReparse_FailsClosed()
    {
        // 预检返回对象带 ReparsePath 供装置断言到具体层。
        Assert.Contains("candidate tree has no reparse point", Lifecycle);
        Assert.Contains("reparse point in candidate tree at", Lifecycle);
        Assert.Contains("candidate ancestor chain unsafe at", Lifecycle);
    }

    // ---- 3. Replace-ASBinary：预检位于任何备份槽创建之前 ----

    [Fact]
    public void D4_Replace_PreCheckBeforeSlotCreation()
    {
        Assert.Contains("Assert-ASCandidateTreeSafe -CandidateDir $candFull -Action 'replace binary'", Lifecycle);
        var replaceStart = Lifecycle.IndexOf("function Replace-ASBinary", StringComparison.Ordinal);
        Assert.True(replaceStart >= 0, "function Replace-ASBinary not found");
        var body = Lifecycle.Substring(replaceStart);
        var preCheck = body.IndexOf("Assert-ASCandidateTreeSafe -CandidateDir $candFull -Action 'replace binary'", StringComparison.Ordinal);
        var slotCreate = body.IndexOf("New-Item -ItemType Directory -Force -Path $slotNew", StringComparison.Ordinal);
        Assert.True(preCheck >= 0, "replace pre-check not found in Replace-ASBinary");
        Assert.True(slotCreate >= 0, "backup slot creation not found in Replace-ASBinary");
        Assert.True(preCheck < slotCreate, "replace pre-check must run BEFORE backup slot creation");
    }

    // ---- 4. Invoke-ASReinstall：预检位于删除旧 appFiles/owner marker/创建安装目录之前 ----

    [Fact]
    public void D4_Reinstall_PreCheckBeforeDestructiveSteps()
    {
        Assert.Contains("Assert-ASCandidateTreeSafe -CandidateDir $candFull -Action 'reinstall from candidate'", Lifecycle);
        var reinstallStart = Lifecycle.IndexOf("function Invoke-ASReinstall", StringComparison.Ordinal);
        Assert.True(reinstallStart >= 0, "function Invoke-ASReinstall not found");
        var body = Lifecycle.Substring(reinstallStart);
        var preCheck = body.IndexOf("Assert-ASCandidateTreeSafe -CandidateDir $candFull -Action 'reinstall from candidate'", StringComparison.Ordinal);
        var removal = body.IndexOf("Remove-ASOwnedFiles -InstallDir $installFull -AppFiles $appFiles", StringComparison.Ordinal);
        var markerRemoval = body.IndexOf("remove ownership marker", StringComparison.Ordinal);
        Assert.True(preCheck >= 0, "reinstall pre-check not found in Invoke-ASReinstall");
        Assert.True(removal >= 0, "Remove-ASOwnedFiles not found in Invoke-ASReinstall");
        Assert.True(preCheck < removal, "reinstall pre-check must run BEFORE deleting old appFiles");
        Assert.True(preCheck < markerRemoval, "reinstall pre-check must run BEFORE removing ownership marker");
    }

    // ---- 5. 既有 D2/D3 候选守卫不破坏（D2/D3 契约字符串保留） ----

    [Fact]
    public void D4_PriorCandidateGuards_StillPresent()
    {
        // D2 候选根自身 reparse 校验 + D3 候选根全祖先链校验必须原样保留。
        Assert.Contains("refusing to use candidate dir: reparse point at", Lifecycle);
        Assert.Contains("Assert-ASFullChainSafe -Path $candFull -Action 'use candidate dir'", Lifecycle);
    }

    // ---- 6. D4 装置覆盖 reinstall/replace + 哈希断言 + 无半安装/无备份残留 ----

    [Fact]
    public void D4_HarnessCoversReinstallReplace_WithHashInvariants()
    {
        Assert.Contains("mklink /J", Harness);
        Assert.Contains("candidate subdir junction", Harness);
        Assert.Contains("reinstall throws (candidate subdir junction)", Harness);
        Assert.Contains("replace throws (candidate subdir junction", Harness);
        // 哈希断言：旧 EXE SHA-256、owner marker 内容、用户文件、outside 哨兵。
        Assert.Contains("SHA256", Harness);
        Assert.Contains("old EXE SHA-256 unchanged", Harness);
        Assert.Contains("owner marker content unchanged", Harness);
        Assert.Contains("user file unchanged", Harness);
        Assert.Contains("outside sentinel + hashes unchanged", Harness);
        // 无半安装 / 无备份残留。
        Assert.Contains("no half-install", Harness);
        Assert.Contains("no rollback-install.new", Harness);
        Assert.Contains("backups\\spkg NOT created (no backup residue)", Harness);
        Assert.Contains("install dir NOT created", Harness);
    }

    // ---- 7. 候选顶层文件 symlink：环境不支持时明确标「环境跳过」，不伪称已执行 ----

    [Fact]
    public void D4_FileSymlink_EnvironmentGated()
    {
        Assert.Contains("candidate top-level file symlink", Harness);
        Assert.Contains("environment does not support file symlink", Harness);
        Assert.Contains("NOT faked as executed", Harness);
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
