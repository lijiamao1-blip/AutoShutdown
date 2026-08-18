using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG D2 junction/symlink/reparse-point 越界防护契约测试（静态文本检查，只读）。
/// 验证 tools/SPkg-Lifecycle.ps1：
///  - 存在统一的「路径在根目录内 + 全链路无 ReparsePoint」校验函数
///    （Test-ASPathWithinRoot / Get-ASRelPathReparsePoint / Get-ASDirTreeSafe / Assert-ASRelPathSafe）；
///  - 所有权门禁、清单删除、空目录清理、候选复制、安装备份、回滚恢复、DataRoot 备份/恢复
///    全部 fail-closed 接入 reparse 校验（命中 junction/symlink/reparse point 即拒绝，
///    不触碰目录外内容，绝不仅作字符串/FullPath 前缀判断）；
///  - D2 装置 Invoke-SPD2ReparseTests.ps1 用临时目录 junction（cmd /c mklink /J，
///    不依赖开发者模式符号链接权限）覆盖 uninstall / reinstall / replace / rollback
///    至少四条路径，并对目录外哨兵文件与 SHA-256 哈希做完全不变断言。
/// 绝不调用生命周期脚本、不写任何目录、不触碰真实系统。
/// </summary>
public sealed class S_PKG_D2_ReparseContractTests
{
    private static readonly string Lifecycle =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "SPkg-Lifecycle.ps1"));

    private static readonly string Harness =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPD2ReparseTests.ps1"));

    // ---- 1. 统一的「根内 + 全链路无 ReparsePoint」校验函数存在 ----

    [Fact]
    public void D2_UnifiedReparseGuardFunctions_Present()
    {
        Assert.Contains("function Test-ASPathWithinRoot", Lifecycle);
        Assert.Contains("function Get-ASRelPathReparsePoint", Lifecycle);
        Assert.Contains("function Get-ASDirTreeSafe", Lifecycle);
        Assert.Contains("function Assert-ASRelPathSafe", Lifecycle);
        // 明确说明不能仅作字符串/FullPath 前缀判断。
        Assert.Contains("not-within-root", Lifecycle);
        Assert.Contains("reparse-point", Lifecycle);
        Assert.Contains("ReparsePoint", Lifecycle);
    }

    // ---- 2. 所有权门禁 fail-closed ----

    [Fact]
    public void D2_OwnershipGate_FailClosedOnReparse()
    {
        Assert.Contains("function Test-ASInstallOwnership", Lifecycle);
        Assert.Contains("install dir itself is a reparse point", Lifecycle);
        Assert.Contains("Get-ASDirTreeSafe -BaseDir $full", Lifecycle);
        Assert.Contains("reparse point under install dir", Lifecycle);
    }

    // ---- 3. 清单删除 fail-closed ----

    [Fact]
    public void D2_ManifestDeletion_FailClosedOnReparse()
    {
        Assert.Contains("function Remove-ASOwnedFiles", Lifecycle);
        Assert.Contains("Test-ASPathWithinRoot -Root $base", Lifecycle);
        Assert.Contains("refusing to remove", Lifecycle);
        Assert.Contains("install dir is a reparse point", Lifecycle);
    }

    // ---- 4. 空目录清理 fail-closed（不跟随 junction 的安全枚举） ----

    [Fact]
    public void D2_EmptyDirCleanup_FailClosedOnReparse()
    {
        Assert.Contains("function Remove-ASEmptyDirsUnder", Lifecycle);
        Assert.Contains("Get-ASDirTreeSafe -BaseDir $base", Lifecycle);
        Assert.Contains("refusing to remove empty dirs", Lifecycle);
    }

    // ---- 5. 候选复制 fail-closed ----

    [Fact]
    public void D2_CandidateCopy_FailClosedOnReparse()
    {
        Assert.Contains("function Copy-ASDirContents", Lifecycle);
        Assert.Contains("Get-ASDirTreeSafe -BaseDir $src", Lifecycle);
        Assert.Contains("Assert-ASRelPathSafe -BaseDir $dst", Lifecycle);
        Assert.Contains("destination is a reparse point", Lifecycle);
        Assert.Contains("refusing to copy from", Lifecycle);
    }

    // ---- 6. 安装备份 fail-closed（不沿 junction 复制目录外内容） ----

    [Fact]
    public void D2_InstallBackup_FailClosedOnReparse()
    {
        Assert.Contains("function Replace-ASBinary", Lifecycle);
        Assert.Contains("Get-ASDirTreeSafe -BaseDir $installFull", Lifecycle);
        Assert.Contains("refusing to back up install dir", Lifecycle);
    }

    // ---- 7. 回滚恢复 fail-closed ----

    [Fact]
    public void D2_RollbackRestore_FailClosedOnReparse()
    {
        Assert.Contains("function Restore-ASInstallBackup", Lifecycle);
        Assert.Contains("function Restore-ASRollback", Lifecycle);
        Assert.Contains("Get-ASRelPathReparsePoint -BaseDir $dataRootFull", Lifecycle);
        Assert.Contains("refusing to restore through reparse point", Lifecycle);
        Assert.Contains("Assert-ASRelPathSafe", Lifecycle);
    }

    // ---- 8. DataRoot 备份/恢复与 UserData=Remove fail-closed ----

    [Fact]
    public void D2_DataRootBackupRestore_FailClosedOnReparse()
    {
        Assert.Contains("function Backup-ASDataRoot", Lifecycle);
        Assert.Contains("refusing to back up data root", Lifecycle);
        Assert.Contains("function Ensure-ASSpkgBackupsRoot", Lifecycle);
        Assert.Contains("Assert-ASRelPathSafe -BaseDir $dataRootFull -RelativePath 'backups\\spkg'", Lifecycle);
        Assert.Contains("function Test-ASSpkgBackupsOwned", Lifecycle);
        Assert.Contains("refusing to remove user data", Lifecycle);
    }

    // ---- 9. 绝不整目录递归删除安装目录（D1 不变量保持） ----

    [Fact]
    public void D2_NoWholeInstallDirRecursiveDelete()
    {
        Assert.DoesNotContain("Remove-Item -LiteralPath $InstallDir -Recurse", Lifecycle);
        Assert.DoesNotContain("Remove-Item $InstallDir -Recurse", Lifecycle);
        Assert.DoesNotContain("$InstallDir -Recurse", Lifecycle);
    }

    // ---- 10. D2 装置用临时目录 junction 覆盖四条路径 + 哨兵/哈希不变 ----

    [Fact]
    public void D2_HarnessCoversFourPaths_WithSentinelHashes()
    {
        Assert.Contains("mklink /J", Harness);
        Assert.Contains("uninstall refuses when install dir contains junction", Harness);
        Assert.Contains("reinstall refuses when install dir contains junction", Harness);
        Assert.Contains("replace refuses when install dir contains junction", Harness);
        Assert.Contains("rollback refuses when install dir contains junction", Harness);
        Assert.Contains("sentinel + hashes unchanged", Harness);
        Assert.Contains("SHA256", Harness);
        Assert.Contains("Get-DirHashSnapshot", Harness);
        Assert.Contains("Test-DirSnapshotEqual", Harness);
        // 覆盖候选目录 / DataRoot 含 junction 的拒绝路径。
        Assert.Contains("candidate / DataRoot junction refusal", Harness);
        // 装置自身带正常路径回归（无 junction 时不误伤）。
        Assert.Contains("normal (junction-free) path regression", Harness);
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
