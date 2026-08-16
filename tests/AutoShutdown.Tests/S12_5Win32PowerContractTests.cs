using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.5 Win32 真实电源契约测试。
/// 静态检查：DllImport 只允许存在于 Win32PowerNativeApi；全源码无 shutdown.exe / Process.Start。
/// 行为测试：Win32PowerService 通过替身 IPowerNativeApi 验证动作映射，绝不调用真实电源。
/// </summary>
public sealed class S12_5Win32PowerContractTests
{
    // ---- 1. 唯一 DllImport 位置 ----

    [Fact]
    public void Win32PowerNativeApi_IsOnlyFileWithDllImport()
    {
        foreach (var file in EnumerateAppSourceFiles())
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
        }
    }

    // ---- 2. Win32PowerNativeApi 不含进程/命令 ----

    [Fact]
    public void Win32PowerNativeApi_NoProcessStartOrShutdownExe()
    {
        var source = ReadAppFile("Infrastructure", "Power", "Win32PowerNativeApi.cs");

        Assert.DoesNotContain("shutdown.exe", source);
        Assert.DoesNotContain("Process.Start", source);
        Assert.DoesNotContain("PowerShell", source);
    }

    // ---- 3. 全源码无真实电源命令 ----

    [Fact]
    public void WholeSource_HasNoShutdownExeOrProcessStart()
    {
        foreach (var file in EnumerateWholeSourceFiles())
        {
            var name = Path.GetFileName(file);
            var content = File.ReadAllText(file);
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

    // ---- 4. Win32PowerService 动作映射（替身验证，无真实调用） ----

    [Theory]
    [InlineData(PowerAction.Shutdown)]
    [InlineData(PowerAction.Restart)]
    [InlineData(PowerAction.Sleep)]
    [InlineData(PowerAction.Hibernate)]
    public async Task Win32PowerService_MapsActionToNative(PowerAction action)
    {
        var native = new RecordingNativeApi();
        var service = new Win32PowerService(native);

        var result = await service.ExecuteAsync(
            new PowerRequest { Action = action, InstanceId = Guid.NewGuid(), Reason = "test" },
            CancellationToken.None);

        Assert.Equal(PowerOutcome.Accepted, result.Outcome);
        Assert.Equal(action, native.LastAction);
        Assert.Equal(1, native.CallCount);
    }

    // ---- 5. Unknown 动作返回 Rejected，不调用原生 ----

    [Fact]
    public async Task Win32PowerService_UnknownAction_RejectedWithoutNativeCall()
    {
        var native = new RecordingNativeApi();
        var service = new Win32PowerService(native);

        var result = await service.ExecuteAsync(
            new PowerRequest { Action = PowerAction.Unknown, InstanceId = Guid.NewGuid(), Reason = "test" },
            CancellationToken.None);

        Assert.Equal(PowerOutcome.Rejected, result.Outcome);
        Assert.Equal(0, native.CallCount);
    }

    // ---- 6. 原生失败返回 Failed 并带错误码 ----

    [Fact]
    public async Task Win32PowerService_NativeFailure_ReturnsFailedWithErrorCode()
    {
        var native = new RecordingNativeApi { Succeed = false, ErrorCode = 87 };
        var service = new Win32PowerService(native);

        var result = await service.ExecuteAsync(
            new PowerRequest { Action = PowerAction.Shutdown, InstanceId = Guid.NewGuid(), Reason = "test" },
            CancellationToken.None);

        Assert.Equal(PowerOutcome.Failed, result.Outcome);
        Assert.Equal(87, result.NativeErrorCode);
        Assert.Equal(1, native.CallCount);
    }

    // ---- Helpers ----

    private sealed class RecordingNativeApi : IPowerNativeApi
    {
        public bool Succeed { get; init; } = true;

        public int? ErrorCode { get; init; }

        public PowerAction? LastAction { get; private set; }

        public int CallCount { get; private set; }

        private (bool, int?) Invoke(PowerAction action)
        {
            LastAction = action;
            CallCount++;
            return (Succeed, Succeed ? null : ErrorCode);
        }

        public (bool Succeeded, int? NativeErrorCode) Shutdown() => Invoke(PowerAction.Shutdown);

        public (bool Succeeded, int? NativeErrorCode) Restart() => Invoke(PowerAction.Restart);

        public (bool Succeeded, int? NativeErrorCode) Sleep() => Invoke(PowerAction.Sleep);

        public (bool Succeeded, int? NativeErrorCode) Hibernate() => Invoke(PowerAction.Hibernate);
    }

    private static string ReadAppFile(params string[] relativeParts)
    {
        var parts = new[] { FindAppSourceRoot() }.Concat(relativeParts).ToArray();
        return File.ReadAllText(Path.Combine(parts));
    }

    private static IEnumerable<string> EnumerateAppSourceFiles()
        => Directory.GetFiles(FindAppSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateWholeSourceFiles()
    {
        var appRoot = FindAppSourceRoot();
        var coreRoot = Path.GetFullPath(Path.Combine(appRoot, "..", "AutoShutdown.Core"));
        return Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
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
