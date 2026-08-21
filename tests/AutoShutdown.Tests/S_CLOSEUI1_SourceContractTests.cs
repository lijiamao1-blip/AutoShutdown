using System.IO;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-CLOSEUI1 源码契约测试。验证「从运行中的进程选择关闭目标」的只读安全边界与执行边界不耦合：
/// 1) 进程选择只读链路（IProcessInfoProvider / DiagnosticProcessInfoProvider / ProcessSelectionGuard /
///    ProcessPickerViewModel / ProcessPickerWindow / RunningProcessRow）不引入任何进程启动、关闭、
///    终止、任务计划、命令解释器、P/Invoke、提权或网络上传；与执行边界 IProcessManager/
///    IAppWindowManager/CloseAppsService 完全分离；
/// 2) 从进程选择添加绝不授予强杀、绝不持久化 PID（仅路径），绝不自动按进程名兜底；
/// 3) 关闭管线固定顺序 OfficeSave→RunCommands→CloseApps 不被改动，唯一电源出口仍是 IPowerService
///    生产路径（ShutdownWorkflow），进程选择不新增任何电源出口；
/// 4) 新增配置字段只读识别信息不参与执行匹配/校验（校验器不读取 ProcessName 等）。
/// </summary>
public sealed class S_CLOSEUI1_SourceContractTests
{
    [Fact]
    public void ProcessSelectionSources_HaveNoProcessControlOrNativeOrShell()
    {
        foreach (var file in ProcessSelectionSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("taskkill", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Stop-Process", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Process.Start", content);
            Assert.DoesNotContain("CloseMainWindow", content); // 只读选择绝不发窗口消息。
            Assert.DoesNotContain("Kill(", content);
            Assert.DoesNotContain("DllImport", content);
            Assert.DoesNotContain("powershell", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cmd.exe", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Register-ScheduledTask", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExitWindowsEx", content);
            Assert.DoesNotContain("SetSuspendState", content);
        }
    }

    [Fact]
    public void ProcessSelectionSources_HaveNoPowerServiceAndNoExecutionBoundaryCoupling()
    {
        foreach (var file in ProcessSelectionSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("IPowerService", content);
            Assert.DoesNotContain("PowerRequest", content);
            Assert.DoesNotContain("IProcessManager", content);
            Assert.DoesNotContain("IAppWindowManager", content);
            Assert.DoesNotContain("CloseAppsService", content);
            Assert.DoesNotContain("ShutdownWorkflow", content);
        }
    }

    [Fact]
    public void PickerAddedRows_NeverGrantForceKill_NeverPersistPid_NoNameFallback()
    {
        var viewModel = File.ReadAllText(Path.Combine(AppSourceRoot(), "Presentation", "MainWindowViewModel.cs"));

        // 从进程选择添加绝不授予强杀。
        Assert.Contains("forceKillAllowed: false", viewModel);
        // 绝不持久化 PID 作为长期目标（选择添加只保留路径）。
        Assert.Contains("processId: null", viewModel);
        Assert.Contains("不持久化 PID", viewModel);
        // 执行匹配唯一依据是规范化完整路径（识别信息仅展示）。
        Assert.Contains("只读识别信息", viewModel);
        Assert.Contains("绝不参与执行匹配", viewModel);
    }

    [Fact]
    public void PickerConfirm_RechecksByPathAndGuard_NoNameFallback_NoAutoUpdate()
    {
        var picker = File.ReadAllText(Path.Combine(AppSourceRoot(), "Presentation", "ProcessPickerViewModel.cs"));

        // 确定时按 PID 重新读取 + 规范化完整路径比对 + 安全资格复核。
        Assert.Contains("_probe.GetById", picker);
        Assert.Contains("ExecutablePathKey.EqualsNormalized", picker);
        Assert.Contains("ProcessSelectionGuard.Evaluate", picker);
        // 找不到匹配进程时绝不自动按进程名兜底：只记录警告并跳过（fail-closed）。
        Assert.Contains("已退出", picker);
        Assert.Contains("不再满足安全校验", picker);
    }

    [Fact]
    public void ConfigIdentityFields_NotReferencedByValidatorOrTargetList()
    {
        var validator = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Configuration", "ConfigurationValidator.cs"));
        var targetList = File.ReadAllText(Path.Combine(CoreSourceRoot(), "CloseApps", "CloseAppsTargetList.cs"));

        Assert.DoesNotContain("ProcessName", validator);
        Assert.DoesNotContain("ProductName", validator);
        Assert.DoesNotContain("CompanyName", validator);
        Assert.DoesNotContain("WindowTitleAtAdd", validator);
        Assert.DoesNotContain("AddedAtUtc", validator);
        Assert.DoesNotContain("ProcessName", targetList);
    }

    [Fact]
    public void ServiceRegistration_PrePipelineOrder_StillOfficeSaveRunCommandsCloseApps()
    {
        var registration = File.ReadAllText(Path.Combine(AppSourceRoot(), "AppHost", "ServiceRegistration.cs"));

        // 固定顺序注释仍在（OfficeSave→RunCommands→CloseApps），未被改动。
        Assert.Contains("OfficeSave", registration);
        Assert.Contains("RunCommands", registration);
        Assert.Contains("CloseApps", registration);
        Assert.Contains("OfficeSave→RunCommands→CloseApps", registration);
    }

    [Fact]
    public void ProcessSelectionSources_NoNetworkUpload()
    {
        foreach (var file in ProcessSelectionSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("HttpClient", content);
            Assert.DoesNotContain("WebClient", content);
            Assert.DoesNotContain("UploadFile", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("POST", content);
        }
    }

    private static IEnumerable<string> ProcessSelectionSources()
    {
        var dir = Path.Combine(AppSourceRoot(), "Infrastructure", "ProcessSelection");
        var sources = new List<string>(Directory.GetFiles(dir, "*.cs"))
        {
            Path.Combine(AppSourceRoot(), "Presentation", "ProcessPickerViewModel.cs"),
            Path.Combine(AppSourceRoot(), "Presentation", "ProcessPickerResult.cs"),
            Path.Combine(AppSourceRoot(), "Presentation", "ProcessPickerPreview.cs"),
            Path.Combine(AppSourceRoot(), "Presentation", "RunningProcessRow.cs"),
            Path.Combine(AppSourceRoot(), "ProcessPickerWindow.xaml.cs"),
            Path.Combine(AppSourceRoot(), "ProcessPickerSummaryWindow.xaml.cs") // S-CLOSEUI1-D1 确认摘要。
        };
        return sources;
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
