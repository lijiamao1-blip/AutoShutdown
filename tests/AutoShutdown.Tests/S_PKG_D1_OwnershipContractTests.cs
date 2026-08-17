using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG D1 安装所有权与破坏性路径加固契约测试（静态文本检查，只读）。
/// 验证 tools/SPkg-Lifecycle.ps1：
///  - 存在安装所有权标记 AutoShutdown.owner.json 及其写入/读取/校验函数；
///  - 危险路径硬守卫 Test-ASForbiddenPath（fs 根 / 用户主目录 / SystemRoot / 仓库根 / artifacts）；
///  - 备份元数据 backup.json 以 sourceInstallDir 绑定该绝对安装路径，跨目录回滚被拒；
///  - 卸载/回滚/重装只按所有权清单 appFiles 逐文件删除，目录仅在为空时删除；
///    绝不 Remove-Item <InstallDir> -Recurse 或枚举整目录全删；
///  - DataRoot 保护：UserData=Remove 只删除经本应用标记（绑定数据根）的 S-PKG 备份。
/// 并验证 D1 所有权测试装置与既有 harness（A-3b/B-2/B-3/B-4）已完成所有权适配。
/// 绝不调用生命周期脚本、不写任何目录、不触碰真实系统。
/// </summary>
public sealed class S_PKG_D1_OwnershipContractTests
{
    private static readonly string Lifecycle =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "SPkg-Lifecycle.ps1"));

    private static readonly string OwnershipHarness =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPD1OwnershipTests.ps1"));

    // ---- 1. 安装所有权标记与门禁存在 ----

    [Fact]
    public void D1_OwnerMarker_And_InstallOwnershipGate_Present()
    {
        Assert.Contains("AutoShutdown.owner.json", Lifecycle);
        Assert.Contains("function Write-ASOwnerMarker", Lifecycle);
        Assert.Contains("function Read-ASOwnerMarker", Lifecycle);
        Assert.Contains("function Test-ASInstallOwnership", Lifecycle);
        // 破坏性操作（替换/回滚/卸载/重装）全部经所有权门禁。
        Assert.Contains("Test-ASInstallOwnership -InstallDir", Lifecycle);
        Assert.Contains("refusing to modify", Lifecycle);
    }

    // ---- 2. 危险路径硬守卫 ----

    [Fact]
    public void D1_ForbiddenPathGuard_Present()
    {
        Assert.Contains("function Test-ASForbiddenPath", Lifecycle);
        Assert.Contains("GetPathRoot", Lifecycle);
        Assert.Contains("UserProfile", Lifecycle);
        Assert.Contains("SystemRoot", Lifecycle);
        Assert.Contains("artifacts", Lifecycle);
    }

    // ---- 3. 备份元数据绑定 sourceInstallDir（跨目录回滚拒绝） ----

    [Fact]
    public void D1_BackupMetaBoundToSourceInstallDir_Present()
    {
        Assert.Contains("backup.json", Lifecycle);
        Assert.Contains("sourceInstallDir", Lifecycle);
        Assert.Contains("function Test-ASBackupBinding", Lifecycle);
        Assert.Contains("backup-path-mismatch", Lifecycle);
    }

    // ---- 4. 清单式删除（appFiles）+ 空目录清理 ----

    [Fact]
    public void D1_ManifestBasedDeletion_Present()
    {
        Assert.Contains("function Remove-ASOwnedFiles", Lifecycle);
        Assert.Contains("function Remove-ASEmptyDirsUnder", Lifecycle);
        Assert.Contains("appFiles", Lifecycle);
    }

    // ---- 5. 绝不整目录递归删除安装目录 ----

    [Fact]
    public void D1_NoWholeInstallDirRecursiveDelete()
    {
        Assert.DoesNotContain("Remove-Item -LiteralPath $InstallDir -Recurse", Lifecycle);
        Assert.DoesNotContain("Remove-Item -Path $InstallDir -Recurse", Lifecycle);
        Assert.DoesNotContain("Remove-Item $InstallDir -Recurse", Lifecycle);
        Assert.DoesNotContain("$InstallDir -Recurse", Lifecycle);
        // 所有权清单刷新只允许针对安装目录本身逐文件删除。
        Assert.Contains("Write-ASOwnerMarker -InstallDir $installFull", Lifecycle);
    }

    // ---- 6. DataRoot 保护（UserData=Remove 只删应用标记的 S-PKG 备份） ----

    [Fact]
    public void D1_DataRootProtection_Present()
    {
        Assert.Contains("function Ensure-ASSpkgBackupsRoot", Lifecycle);
        Assert.Contains("function Test-ASSpkgBackupsOwned", Lifecycle);
        Assert.Contains("spkg-backups-root", Lifecycle);
        Assert.Contains("refusing to remove user data", Lifecycle);
    }

    // ---- 7. D1 所有权测试装置覆盖拒绝/哨兵/有效所有权/fail-closed/DataRoot 误指 ----

    [Fact]
    public void D1_OwnershipHarnessCoversRejectSentinelFailClosed()
    {
        Assert.Contains("forbidden path guards", OwnershipHarness);
        Assert.Contains("ownership state machine", OwnershipHarness);
        Assert.Contains("fail-closed rejection", OwnershipHarness);
        Assert.Contains("valid ownership operations", OwnershipHarness);
        Assert.Contains("data-root misdirection", OwnershipHarness);
        Assert.Contains("no-marker", OwnershipHarness);
        Assert.Contains("bad-marker", OwnershipHarness);
        Assert.Contains("path-mismatch", OwnershipHarness);
    }

    // ---- 9. 逐子项合并复制（候选/备份子目录复制到已存在同名目录时绝不嵌套）----

    [Fact]
    public void D1_NoNestingMergeCopy_Present()
    {
        Assert.Contains("function Copy-ASDirContents", Lifecycle);
        Assert.Contains("Copy-ASDirContents -SourceDir", Lifecycle);
        Assert.Contains("nesting regression", OwnershipHarness);
    }

    // ---- 8. 既有 harness（A-3b/B-2/B-3/B-4）已完成所有权标记适配 ----

    [Fact]
    public void D1_ExistingHarnessesSeededWithOwnerMarker()
    {
        var lifecycleHarness = File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPkgLifecycleTests.ps1"));
        var upgradeHarness = File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPkgUpgradeTests.ps1"));
        var boundaryHarness = File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPBoundaryTests.ps1"));
        var smokeHarness = File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-ASReleaseSmoke.ps1"));
        Assert.Contains("Write-ASOwnerMarker", lifecycleHarness);
        Assert.Contains("Write-ASOwnerMarker", upgradeHarness);
        Assert.Contains("Write-ASOwnerMarker", boundaryHarness);
        Assert.Contains("Write-ASOwnerMarker", smokeHarness);
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
