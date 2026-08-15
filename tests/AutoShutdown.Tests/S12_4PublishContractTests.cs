using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.4 publish contract tests. Static text checks against the publish
/// script and the Chinese release notes. These tests are read-only and never
/// invoke publish, never touch the registry and never start the app.
/// </summary>
public sealed class S12_4PublishContractTests
{
    // ---- 1. 发布脚本不包含真实电源命令 ----

    [Fact]
    public void PublishScript_HasNoRealPowerCommands()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");

        Assert.DoesNotContain("Win32PowerService", script);
        Assert.DoesNotContain("shutdown.exe", script);
        Assert.DoesNotContain("ExitWindowsEx", script);
        Assert.DoesNotContain("SetSuspendState", script);
    }

    // ---- 2. 发布脚本不写注册表、不启用自启动 ----

    [Fact]
    public void PublishScript_DoesNotWriteRegistryOrEnableAutoStart()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");

        Assert.DoesNotContain("Registry.CurrentUser", script);
        Assert.DoesNotContain("Microsoft.Win32", script);
        Assert.DoesNotContain("CurrentVersion\\Run", script);
    }

    // ---- 3. 发布参数明确关闭 Trim、SingleFile、ReadyToRun ----

    [Fact]
    public void PublishParams_DisableTrimSingleFileAndReadyToRun()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");

        Assert.Contains("PublishSingleFile=false", script);
        Assert.Contains("PublishTrimmed=false", script);
        Assert.Contains("PublishReadyToRun=false", script);
        Assert.Contains("DebugType=None", script);
        Assert.Contains("DebugSymbols=false", script);
    }

    // ---- 4. 禁止文件清单生效 ----

    [Fact]
    public void ForbiddenFileList_IsAppliedInStagingCheck()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");

        Assert.Contains("*.pdb", script);
        Assert.Contains("*.trx", script);
        Assert.Contains("testhost", script);
        Assert.Contains("xunit", script);
        Assert.Contains("TestResults", script);
        Assert.Contains("s12-", script);
    }

    // ---- 5. ZIP 命名和版本号规则正确 ----

    [Fact]
    public void ZipNaming_FollowsVersionRule()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");

        Assert.Contains("AutoShutdown-v$version-win-x64-framework-dependent.zip", script);
        Assert.Contains("AutoShutdown-v$version-win-x64-self-contained.zip", script);
        Assert.Contains("$version = '1.0.0'", script);
    }

    // ---- 6. SHA-256 清单格式正确 ----

    [Fact]
    public void Sha256Manifest_IsGenerated()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");

        Assert.Contains("SHA256SUMS.txt", script);
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("-Algorithm SHA256", script);
    }

    // ---- 7. 说明文件明确 FakePowerService/安全测试模式 ----

    [Fact]
    public void ReleaseNotes_StateFakePowerAndSafeTestMode()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");
        var notes = ReadToolFile("安全发布说明.txt");

        Assert.Contains("FakePowerService", script);
        Assert.Contains("FakePowerService", notes);
        Assert.Contains("安全测试模式", notes);
        Assert.Contains("不会执行真实关机、重启、睡眠或休眠", notes);
    }

    // ---- 8. 发布包排除源码、测试、PDB、诊断和用户数据 ----

    [Fact]
    public void ReleaseNotes_ExcludeSourceTestPdbDiagnosticsAndUserData()
    {
        var script = ReadToolFile("Publish-SafeRelease.ps1");
        var notes = ReadToolFile("安全发布说明.txt");

        // 脚本：备份只复制已验收构建产物，打包只压缩 staging，绝不包含 src/tests。
        Assert.Contains("artifacts\\release\\v$version", script);
        Assert.Contains("Compress-Archive", script);
        // 说明：发布包不含源码/测试/PDB/诊断/用户数据。
        Assert.Contains("不含源码、测试、PDB、诊断日志、dump、TRX", notes);
        Assert.Contains("不含任何用户配置、运行态或日志数据", notes);
    }

    // ---- Helpers ----

    private static string ReadToolFile(string fileName)
        => File.ReadAllText(Path.Combine(FindProjectRoot(), "tools", fileName));

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown project root was not found.");
    }
}
