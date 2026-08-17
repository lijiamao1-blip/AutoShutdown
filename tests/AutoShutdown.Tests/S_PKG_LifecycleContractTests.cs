using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-PKG 生命周期状态机契约测试（静态文本检查，只读）。
/// 验证 tools/SPkg-Lifecycle.ps1：
///  - 纯文件操作：不含真实电源命令 / 注册表 / 自启 / 防火墙 / 任务计划操作；
///  - 备份成功是替换前置（_complete.marker 门禁），失败不得继续；
///  - JSON 健康分类区分 NotFound/Corrupt/Invalid/UnsupportedVersion，绝不静默回退；
///  - 回滚只在与发布一一对应的完整备份存在时执行（否则拒绝触碰安装目录）；
///  - 卸载对用户数据 Keep/Remove 显式二选一；
///  - 命令行分发受点源守卫保护。
/// 绝不调用生命周期脚本、不写任何目录、不触碰真实系统。
/// </summary>
public sealed class S_PKG_LifecycleContractTests
{
    private static readonly string Lifecycle =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "SPkg-Lifecycle.ps1"));

    private static readonly string Harness =
        File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPkgLifecycleTests.ps1"));

    // ---- 1. 生命周期状态机是纯文件操作 ----

    [Fact]
    public void Lifecycle_HasNoRealPowerCommands()
    {
        Assert.DoesNotContain("shutdown.exe", Lifecycle);
        Assert.DoesNotContain("Stop-Computer", Lifecycle);
        Assert.DoesNotContain("Restart-Computer", Lifecycle);
        Assert.DoesNotContain("SetSuspendState", Lifecycle);
        Assert.DoesNotContain("IPowerService", Lifecycle);
    }

    [Fact]
    public void Lifecycle_DoesNotTouchRegistryOrAutoStart()
    {
        Assert.DoesNotContain("Registry.CurrentUser", Lifecycle);
        Assert.DoesNotContain("Microsoft.Win32", Lifecycle);
        Assert.DoesNotContain("CurrentVersion\\Run", Lifecycle);
        Assert.DoesNotContain("AutoStartService", Lifecycle);
        Assert.DoesNotContain("StartWithWindows", Lifecycle);
    }

    [Fact]
    public void Lifecycle_DoesNotTouchFirewallOrTaskScheduler()
    {
        Assert.DoesNotContain("New-NetFirewallRule", Lifecycle);
        Assert.DoesNotContain("netsh advfirewall", Lifecycle);
        Assert.DoesNotContain("Register-ScheduledTask", Lifecycle);
        Assert.DoesNotContain("schtasks", Lifecycle);
        Assert.DoesNotContain("TaskScheduler", Lifecycle);
    }

    // ---- 2. 备份成功是任何替换前置；失败不得继续 ----

    [Fact]
    public void Lifecycle_BackupGatesReplace()
    {
        // 替换前必须完成备份并写入完整标记：备份阶段失败在候选复制之前抛错。
        Assert.Contains("_complete.marker", Lifecycle);
        Assert.Contains("backup failed; original install untouched", Lifecycle);
        Assert.Contains("install backup incomplete", Lifecycle);
        Assert.Contains("binary replace failed and was rolled back", Lifecycle);
    }

    [Fact]
    public void Lifecycle_RollbackRequiresCompleteBackupOrRefuses()
    {
        // 回滚只在完整备份存在时执行；备份不完整则拒绝触碰安装目录（fail-closed）。
        Assert.Contains("_complete.marker", Lifecycle);
        Assert.Contains("_no-install.marker", Lifecycle);
        Assert.Contains("refusing to modify", Lifecycle);
    }

    // ---- 3. JSON 健康分类（绝不把损坏当默认） ----

    [Fact]
    public void Lifecycle_JsonHealthDistinguishesAllFailureClasses()
    {
        Assert.Contains("NotFound", Lifecycle);
        Assert.Contains("Corrupt", Lifecycle);
        Assert.Contains("Invalid", Lifecycle);
        Assert.Contains("UnsupportedVersion", Lifecycle);
        // 损坏/缺失数据绝不静默回退为可能触发任务的默认值。
        Assert.DoesNotContain("ConvertFrom-Json | ForEach", Lifecycle);
    }

    [Fact]
    public void Lifecycle_HealthChecksRuntimeNoSchemaButConfigAndTasksExpected()
    {
        // config.json=1、tasks.json=2、runtime.json 只要求可解析（V1 单实例/V2 多实例）。
        Assert.Contains("'config.json' = 1", Lifecycle);
        Assert.Contains("'tasks.json' = 2", Lifecycle);
        Assert.Contains("runtime.json", Lifecycle);
    }

    // ---- 4. 卸载对用户数据显式二选一；命令行分发受点源守卫保护 ----

    [Fact]
    public void Lifecycle_UninstallChoosesUserDataExplicitly()
    {
        Assert.Contains("ValidateSet('Keep', 'Remove')", Lifecycle);
        Assert.Contains("$UserData", Lifecycle);
        Assert.Contains("Invoke-ASUninstall", Lifecycle);
    }

    [Fact]
    public void Lifecycle_CliDispatchGuardedByDotSourceCheck()
    {
        Assert.Contains("$MyInvocation.InvocationName -ne '.'", Lifecycle);
        Assert.Contains("switch ($Command)", Lifecycle);
    }

    // ---- 5. 升级编排 Invoke-ASUpgrade：备份前置、迁移钩子、自检门禁、失败自动回滚 ----

    [Fact]
    public void Lifecycle_UpgradeOrchestrationIsBackupFirstAndSelfCheckGated()
    {
        Assert.Contains("function Invoke-ASUpgrade", Lifecycle);
        Assert.Contains("Backup-ASDataRoot -Root $dataRoot -Tag $Tag", Lifecycle);
        Assert.Contains("Replace-ASBinary", Lifecycle);
        Assert.Contains("Test-ASSelfCheck", Lifecycle);
        // 迁移失败 / 自检失败都必须自动回滚（绝不带着半迁移数据继续）。
        Assert.Contains("upgrade data migration failed; automatic rollback performed", Lifecycle);
        Assert.Contains("upgrade self-check failed; automatic rollback performed", Lifecycle);
        // 回滚标签精确匹配尾部：{ts}-<Tag>，避免 'upgrade' 误配 'upgrade2'。
        Assert.Contains("\"*-$Tag\"", Lifecycle);
    }

    [Fact]
    public void Lifecycle_ReplaceClearsStaleVersionedExe_AndSelfCheckMatchesExpectedStrictly()
    {
        // 替换后清除安装目录中与候选同名之外的过期 AutoShutdown-v*.exe（避免新旧并存）。
        Assert.Contains("candExeNames", Lifecycle);
        Assert.Contains("Where-Object { $candExeNames -notcontains $_.Name }", Lifecycle);
        // 自检严格匹配 ExpectedVersion，且把陈旧版本化 EXE 视为失败。
        Assert.Contains("stale versioned exe present", Lifecycle);
        Assert.Contains("expected $ExpectedVersion not found", Lifecycle);
    }

    // ---- 6. 测试装置覆盖核心状态机路径 ----

    [Fact]
    public void Lifecycle_HarnessExercisesCorePaths()
    {
        Assert.Contains("replace failure injection", Harness);
        Assert.Contains("rollback restores old exe", Harness);
        Assert.Contains("uninstall Remove clears backups", Harness);
        Assert.Contains("reinstall keeps data root", Harness);
        Assert.Contains("backup failed; original install untouched", Lifecycle);
    }

    // ---- 7. B-2 升级装置覆盖 V1→V2 迁移与失败回退路径 ----

    [Fact]
    public void Lifecycle_UpgradeHarnessExercisesB2ValidationSet()
    {
        var upgradeHarness = File.ReadAllText(Path.Combine(FindToolsRoot(), "test", "Invoke-SPkgUpgradeTests.ps1"));
        Assert.Contains("V1->V2 normal upgrade", upgradeHarness);
        Assert.Contains("runtime.json.v1bak", upgradeHarness);
        Assert.Contains("corrupt config", upgradeHarness);
        Assert.Contains("corrupt tasks", upgradeHarness);
        Assert.Contains("corrupt runtime", upgradeHarness);
        Assert.Contains("backup failure aborts upgrade", upgradeHarness);
        Assert.Contains("replace failure rolled back to V1 exe", upgradeHarness);
        Assert.Contains("self-check failure rolled back to V1 exe", upgradeHarness);
        Assert.Contains("V2->V2 re-upgrade", upgradeHarness);
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
