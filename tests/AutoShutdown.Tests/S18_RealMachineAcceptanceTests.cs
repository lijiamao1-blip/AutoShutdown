using System.Diagnostics;
using AutoShutdown.App.Infrastructure.CloseApps;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18 C4 真机验收（门控）。仅在环境变量 S18_REALMACHINE=1 时触发真实进程，否则为 no-op
/// （默认安全：自动化套件绝不触碰真实用户应用）。验收目标：真实 DiagnosticProcessManager /
/// DiagnosticAppWindowManager 能对「测试专属、可恢复」的 Notepad 实例优雅关闭（仅 WM_CLOSE，
/// 绝不强杀、绝不触碰其他进程）。Process.Start 仅用于启动本测试专属实例（测试项目内，
/// 不违反 App 源码冻结契约）。
/// </summary>
[Trait("Category", "RealMachine")]
public sealed class S18_RealMachineAcceptanceTests
{
    [Fact]
    public void Notepad_DedicatedInstance_ClosesGracefully_WithoutForceKill()
    {
        if (!RealMachineEnabled())
        {
            return; // 门控：需 S18_REALMACHINE=1 才执行真机验收。
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"s18-closeapps-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFile, string.Empty);

        using var notepad = StartNotepad(tempFile);
        Assert.NotNull(notepad);

        try
        {
            var processManager = new DiagnosticProcessManager();
            var windowManager = new DiagnosticAppWindowManager();

            // 等待并确认真实进程枚举能按 PID 读取到该进程。
            var snapshot = WaitFor(
                () => processManager.GetProcessById(notepad.Id),
                TimeSpan.FromSeconds(10));
            Assert.NotNull(snapshot);
            Assert.Equal(notepad.Id, snapshot.ProcessId);

            // 仅优雅关闭（WM_CLOSE），绝不强杀。
            var closed = false;
            for (var attempt = 0; attempt < 10 && !closed; attempt++)
            {
                if (windowManager.HasExited(notepad.Id))
                {
                    closed = true;
                    break;
                }

                windowManager.RequestClose(notepad.Id);
                closed = windowManager.WaitForExit(notepad.Id, TimeSpan.FromSeconds(2));
            }

            Assert.True(closed, "The dedicated Notepad instance did not close gracefully.");
            Assert.True(windowManager.HasExited(notepad.Id));
        }
        finally
        {
            Cleanup(notepad, tempFile);
        }
    }

    private static bool RealMachineEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("S18_REALMACHINE"), "1", StringComparison.Ordinal);

    private static Process? StartNotepad(string tempFile)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
                Arguments = $"\"{tempFile}\"",
                UseShellExecute = false
            });
        }
        catch
        {
            return null; // 真机环境不可用：交由人工验收。
        }
    }

    private static void Cleanup(Process notepad, string tempFile)
    {
        // 仅清理本测试专属实例（可恢复、非用户数据）；绝不触碰其他进程。
        try
        {
            if (!notepad.HasExited)
            {
                notepad.Kill();
                notepad.WaitForExit(2000);
            }
        }
        catch
        {
            // 清理失败可忽略：测试专属实例，不影响其他进程。
        }

        try
        {
            File.Delete(tempFile);
        }
        catch
        {
        }
    }

    private static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            Thread.Sleep(250);
        }

        return null;
    }
}
