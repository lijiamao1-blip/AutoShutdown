using System.Text.RegularExpressions;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class AppHostSourceContractTests
{
    [Fact]
    public void AppCompositionRoot_RegistersGuardedPowerServiceWithFakeAndWin32()
    {
        var source = ReadAppFile("AppHost", "ServiceRegistration.cs");

        Assert.Contains("AddSingleton<FakePowerService>", source);
        Assert.Contains("AddSingleton<IPowerNativeApi, Win32PowerNativeApi>", source);
        Assert.Contains("AddSingleton<Win32PowerService>", source);
        Assert.Contains("AddSingleton<IPowerService>(provider =>", source);
        Assert.Contains("new GuardedPowerService(", source);
        Assert.DoesNotContain("AddSingleton<IPowerService, FakePowerService>", source);
    }

    [Fact]
    public void AppSources_PowerDllImportsOnlyInWin32PowerNativeApi()
    {
        foreach (var file in EnumerateAppSources())
        {
            var name = Path.GetFileName(file);
            var content = File.ReadAllText(file);

            if (name == "Win32PowerNativeApi.cs")
            {
                Assert.Contains("DllImport", content);
            }
            else
            {
                Assert.DoesNotContain("DllImport", content);
                Assert.DoesNotContain("ExitWindowsEx", content);
                Assert.DoesNotContain("SetSuspendState", content);
            }

            Assert.DoesNotContain("shutdown.exe", content);
            if (name == "OfficeSaveHelperLauncher.cs")
            {
                Assert.Contains("Process.Start", content);
            }
            else
            {
                Assert.DoesNotContain("Process.Start", content);
            }
        }
    }

    [Fact]
    public void AppXaml_DoesNotSetStartupUri()
    {
        var source = ReadAppFile("App.xaml");

        Assert.DoesNotContain("StartupUri", source);
    }

    [Fact]
    public void SecondaryBranch_ComesBeforeFullDiSchedulerTrayAndWindowStartup()
    {
        var source = ReadAppFile("App.xaml.cs");

        var secondaryExit = source.IndexOf("Shutdown()", StringComparison.Ordinal);
        Assert.True(secondaryExit >= 0, "Secondary branch must call Shutdown().");

        var diBuild = source.IndexOf("AddAutoShutdownServices", StringComparison.Ordinal);
        var coordinatorStart = source.IndexOf("_coordinator.Start()", StringComparison.Ordinal);

        Assert.True(secondaryExit < diBuild, "Secondary exit must happen before building the full DI container.");
        Assert.True(diBuild >= 0, "Primary branch must build the DI container.");
        Assert.True(coordinatorStart > diBuild, "Coordinator start must happen after DI is built.");

        var coordinator = ReadAppFile("AppHost", "ApplicationLifetimeCoordinator.cs");
        var runIndex = coordinator.IndexOf("RunAsync", StringComparison.Ordinal);
        var trayIndex = coordinator.IndexOf("_trayIcon.Start()", StringComparison.Ordinal);
        var windowIndex = coordinator.IndexOf("ActivateMainWindow", StringComparison.Ordinal);

        Assert.True(runIndex >= 0, "Coordinator must start the scheduler.");
        Assert.True(trayIndex > runIndex, "Tray must start after the scheduler.");
        Assert.True(windowIndex > trayIndex, "Window activation must happen last.");
    }

    [Fact]
    public void SchedulerEngineRunAsync_HasExactlyTwoCallSites()
    {
        var matches = 0;
        foreach (var file in EnumerateAppSources())
        {
            var content = File.ReadAllText(file);
            matches += Regex.Matches(content, @"RunAsync\s*\(").Count;
        }

        // 恰好两个合法调用点：UI 启动路径（协调器）与 headless 外部触发路径（App.xaml.cs，
        // S22-D2：外部触发须先启动本地调度引擎，再经唯一接入/仲裁路径执行）。不允许第三处。
        Assert.Equal(2, matches);
        Assert.Contains("RunAsync", ReadAppFile("AppHost", "ApplicationLifetimeCoordinator.cs"));
        Assert.Contains("RunAsync", ReadAppFile("App.xaml.cs"));
    }

    [Fact]
    public void PipeProtocol_OnlyAllowsActivateAndTrigger_WithLengthAndTimeoutLimits()
    {
        var server = ReadAppFile("Infrastructure", "ActivationPipeServer.cs");
        var client = ReadAppFile("Infrastructure", "ActivationPipeClient.cs");

        // 合法协议仅两条命令：ACTIVATE（激活窗口）与 TRIGGER <本地 task id>（S22 外部触发
        // 转发）。两条都不直接触碰电源/工作流/存储/配置——TRIGGER 只把稳定本地 id 交给
        // ExternalTaskTriggerService 裁决（fail-closed），绝不反向覆盖本地或执行电源。
        Assert.Contains("\"ACTIVATE\"", server);
        Assert.Contains("\"TRIGGER \"", server);
        Assert.Contains("ExternalTaskTriggerService", server);
        Assert.Contains("HandleExternalTriggerAsync", server);
        Assert.Contains("MaxMessageBytes", server);
        Assert.Contains("NamedPipeServerStream", server);
        Assert.DoesNotContain("SchedulerEngine", server);
        Assert.DoesNotContain("Storage", server);
        Assert.DoesNotContain("Configuration", server);
        Assert.DoesNotContain("IPowerService", server);
        Assert.DoesNotContain("ShutdownWorkflow", server);
        Assert.DoesNotContain("ExitWindowsEx", server);

        Assert.Contains("TotalTimeoutMilliseconds", client);
        Assert.Contains("CancelAfter", client);
        Assert.Contains("\"ACTIVATE\"", client);
        Assert.Contains("TryTriggerAsync", client);
    }

    [Fact]
    public void TrayMenu_HasNoPowerCommands()
    {
        var source = ReadAppFile("Infrastructure", "TrayIconService.cs");

        Assert.Contains("打开控制面板", source);
        Assert.Contains("退出程序", source);
        Assert.DoesNotContain("立即关机", source);
        Assert.DoesNotContain("重启", source);
        Assert.DoesNotContain("睡眠", source);
        Assert.DoesNotContain("休眠", source);
        Assert.DoesNotContain("Restart", source);
        Assert.DoesNotContain("Sleep", source);
        Assert.DoesNotContain("Hibernate", source);
    }

    [Fact]
    public void WindowClosing_HidesInsteadOfExitingWhenNotQuitting()
    {
        var source = ReadAppFile("MainWindow.xaml.cs");

        Assert.Contains("e.Cancel = true", source);
        Assert.Contains("Hide()", source);
        Assert.Contains("IsExiting", source);
    }

    [Fact]
    public void LifetimeCleanup_IsIdempotent_AndDisposesTrayPipeMutexAndProvider()
    {
        var coordinator = ReadAppFile("AppHost", "ApplicationLifetimeCoordinator.cs");
        var app = ReadAppFile("App.xaml.cs");

        Assert.Contains("TryBeginExit", coordinator);
        Assert.Contains("_trayIcon.Dispose", coordinator);
        Assert.Contains("_pipeServer.DisposeAsync", coordinator);
        Assert.Contains("_singleInstance.Dispose", coordinator);
        Assert.Contains("_appCts.Dispose", coordinator);

        Assert.Contains("(_serviceProvider as IDisposable)?.Dispose()", app);
    }

    private static string ReadAppFile(params string[] relativeParts)
    {
        var parts = new[] { FindAppSourceRoot() }
            .Concat(relativeParts)
            .ToArray();

        return File.ReadAllText(Path.Combine(parts));
    }

    private static IEnumerable<string> EnumerateAppSources()
    {
        var root = FindAppSourceRoot();
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindAppSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown.App source directory was not found.");
    }
}
