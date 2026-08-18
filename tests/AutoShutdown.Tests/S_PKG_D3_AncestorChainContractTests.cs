using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG D3 父链 junction 越界防护契约测试（静态文本检查，只读）。
/// 验证 tools/SPkg-Lifecycle.ps1：
///  - 存在统一的绝对路径祖先链守卫 Test-ASFullChainSafe / Assert-ASFullChainSafe：
///    从卷根开始逐分量探测到目标；保留调用者传入的词法路径（Path.GetFullPath 纯词法，
///    绝不先解析为物理目标）；对不存在的目标检查到最后一个已存在祖先目录。
///  - 所有权门禁、清单删除、空目录清理、候选复制、安装备份、回滚恢复、
///    DataRoot 备份/恢复/删除全部接入该祖先链守卫（fail-closed）。
///  - D3 装置 Invoke-SPD3AncestorTests.ps1 用「父目录 junction -> outside」的 alias\install
///    覆盖 ownership/uninstall/reinstall/replace/rollback，并对目录外哨兵文件 SHA-256
///    做完全不变断言；覆盖 CandidateDir / DataRoot 位于父 junction 下的拒绝。
///  - UserProfile 为空的非交互环境脆弱性已修复（安全跳过该专属断言，不因空路径异常中断，
///    且不降低真实桌面环境覆盖）。
/// 绝不调用生命周期脚本、不写任何目录、不触碰真实系统。
/// </summary>
public sealed class S_PKG_D3_AncestorChainContractTests
{
    private static readonly string Lifecycle =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "SPkg-Lifecycle.ps1"));

    private static readonly string Harness =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPD3AncestorTests.ps1"));

    private static readonly string D1Harness =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPD1OwnershipTests.ps1"));

    // ---- 1. 统一祖先链守卫存在：卷根逐分量、词法路径、不先解析物理目标 ----

    [Fact]
    public void D3_UnifiedAncestorChainGuard_Present()
    {
        Assert.Contains("function Test-ASFullChainSafe", Lifecycle);
        Assert.Contains("function Assert-ASFullChainSafe", Lifecycle);
        // 从卷根开始逐分量探测。
        Assert.Contains("[System.IO.Path]::GetPathRoot($full)", Lifecycle);
        // 基于调用者传入的词法路径（Resolve-ASPath = Path.GetFullPath，纯词法，不跟随 junction）。
        Assert.Contains("$full = Resolve-ASPath $Path", Lifecycle);
        // 逐分量用 -LiteralPath 探测 reparse 属性。
        Assert.Contains("Get-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue", Lifecycle);
    }

    // ---- 2. 不得先解析为物理目标；禁止 Resolve-Path ----

    [Fact]
    public void D3_NoPhysicalResolutionBeforeProbing()
    {
        Assert.DoesNotContain("Resolve-Path", Lifecycle);
        Assert.Contains("ReparsePoint", Lifecycle);
    }

    // ---- 3. 不存在的目标：检查到最后一个已存在祖先目录 ----

    [Fact]
    public void D3_NonExistentTarget_ChecksToLastExistingAncestor()
    {
        // 分量不存在即停止（此时其前的已存在祖先已全部探测过；reparse 祖先早已命中返回）。
        Assert.Contains("if ($null -eq $item) { break }", Lifecycle);
    }

    // ---- 4. 七类破坏性/递归路径全部接入祖先链守卫 ----

    [Fact]
    public void D3_OwnershipGate_WiresAncestorChain()
    {
        Assert.Contains("function Test-ASInstallOwnership", Lifecycle);
        Assert.Contains("install dir chain unsafe", Lifecycle);
        Assert.Contains("Test-ASFullChainSafe -Path $full", Lifecycle);
        Assert.Contains("Reason = 'reparse-point'", Lifecycle);
    }

    [Fact]
    public void D3_ManifestDeletion_WiresAncestorChain()
    {
        // Remove-ASOwnedFiles 经 Test-ASPathWithinRoot（其内部升级为全链路探测）+ 自身断言。
        Assert.Contains("function Remove-ASOwnedFiles", Lifecycle);
        Assert.Contains("Assert-ASFullChainSafe -Path $base -Action 'remove owned files'", Lifecycle);
    }

    [Fact]
    public void D3_EmptyDirCleanup_WiresAncestorChain()
    {
        Assert.Contains("function Remove-ASEmptyDirsUnder", Lifecycle);
        // 经 Get-ASDirTreeSafe（内部做 base 全链路探测）。
        Assert.Contains("Get-ASDirTreeSafe -BaseDir $base", Lifecycle);
    }

    [Fact]
    public void D3_CandidateCopy_WiresAncestorChain()
    {
        Assert.Contains("function Copy-ASDirContents", Lifecycle);
        Assert.Contains("reparse point in ancestor chain at '$($dstChain.ReparsePath)'", Lifecycle);
        // 目标链探测在复制前进行。
        Assert.Contains("$dstChain = Test-ASFullChainSafe -Path $dst", Lifecycle);
    }

    [Fact]
    public void D3_InstallBackup_WiresAncestorChain()
    {
        Assert.Contains("function Replace-ASBinary", Lifecycle);
        // 数据根与候选目录均在入口处做祖先链断言。
        Assert.Contains("Assert-ASFullChainSafe -Path $dataRootFull -Action 'use data root'", Lifecycle);
        Assert.Contains("Assert-ASFullChainSafe -Path $candFull -Action 'use candidate dir'", Lifecycle);
    }

    [Fact]
    public void D3_RollbackRestore_WiresAncestorChain()
    {
        Assert.Contains("function Restore-ASRollback", Lifecycle);
        Assert.Contains("Assert-ASFullChainSafe -Path $dataRootFull -Action 'use data root'", Lifecycle);
    }

    [Fact]
    public void D3_DataRootBackupRestore_WiresAncestorChain()
    {
        Assert.Contains("function Backup-ASDataRoot", Lifecycle);
        Assert.Contains("Assert-ASFullChainSafe -Path $dataRootFull -Action 'back up data root'", Lifecycle);
        // Invoke-ASUninstall(UserData=Remove) 数据根链守卫。
        Assert.Contains("function Invoke-ASUninstall", Lifecycle);
        Assert.Contains("Assert-ASFullChainSafe -Path $dataRootFull -Action 'use data root'", Lifecycle);
    }

    [Fact]
    public void D3_ReinstallCandidate_WiresAncestorChain()
    {
        Assert.Contains("function Invoke-ASReinstall", Lifecycle);
        Assert.Contains("Assert-ASFullChainSafe -Path $candFull -Action 'use candidate dir'", Lifecycle);
    }

    // ---- 5. 共享原语内部升级为全链路探测 ----

    [Fact]
    public void D3_SharedPrimitives_UseFullChain()
    {
        Assert.Contains("$chain = Test-ASFullChainSafe -Path $full", Lifecycle);   // Test-ASPathWithinRoot
        Assert.Contains("$chain = Test-ASFullChainSafe -Path $base", Lifecycle);   // Get-ASDirTreeSafe
        Assert.Contains("$baseChain = Test-ASFullChainSafe -Path $base", Lifecycle); // Get-ASRelPathReparsePoint
    }

    // ---- 6. D3 装置覆盖五条路径 + 哨兵/哈希 + CandidateDir/DataRoot 父 junction ----

    [Fact]
    public void D3_HarnessCoversLifecyclePaths_WithSentinelHashes()
    {
        Assert.Contains("mklink /J", Harness);
        Assert.Contains("ownership gate rejects install dir under ancestor junction", Harness);
        Assert.Contains("uninstall refuses when install dir is under ancestor junction", Harness);
        Assert.Contains("reinstall refuses when install dir is under ancestor junction", Harness);
        Assert.Contains("replace refuses when install dir is under ancestor junction", Harness);
        Assert.Contains("rollback refuses when install dir is under ancestor junction", Harness);
        Assert.Contains("candidate dir under ancestor junction refused", Harness);
        Assert.Contains("data root under ancestor junction refused", Harness);
        Assert.Contains("non-existent target under ancestor junction refused", Harness);
        Assert.Contains("outside sentinel + hashes unchanged", Harness);
        Assert.Contains("SHA256", Harness);
        Assert.Contains("Get-DirHashSnapshot", Harness);
        Assert.Contains("Test-DirSnapshotEqual", Harness);
        // 装置自身带正常路径回归（无 junction 时不误伤）。
        Assert.Contains("normal (junction-free) path regression", Harness);
    }

    // ---- 7. UserProfile 为空脆弱性已修复（D1 装置 + 生命周期守卫） ----

    [Fact]
    public void D3_UserProfileEmptyFragility_Fixed()
    {
        // D1 装置：UserProfile 为空时安全跳过，不因空路径绑定异常中断。
        Assert.Contains("SKIP  user home forbidden (UserProfile empty", D1Harness);
        // 真实桌面环境覆盖不降低：非空时仍执行原断言。
        Assert.Contains("Assert-True 'user home forbidden' (Test-ASForbiddenPath $userProfile)", D1Harness);
        // 生命周期守卫：UserProfile 为空时跳过比较，避免 GetFullPath('') 异常中断。
        Assert.Contains("IsNullOrWhiteSpace($userProfile)", Lifecycle);
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
