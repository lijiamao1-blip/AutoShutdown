using System.IO;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S20 源码契约测试。验证无人值守的边界与安全不变量：
/// 1) Unattended 全部 Core 类型不触碰 IPowerService / PowerRequest / PowerResult
///    （唯一电源出口保持，绝无新增电源调用方）。
/// 2) 无 P/Invoke、无 shell 逃逸、无进程启动、无网络/远程写入旁路
///    （远程层仅能消费只读结论，不得启用/修改/续期）。
/// 3) IUnattendedPolicyService 是唯一的读写接口：EvaluateAsync（读）+ Enable/Revoke（本地写）。
/// </summary>
public sealed class S20_UnattendedSourceContractTests
{
    [Fact]
    public void UnattendedCoreTypes_HaveNoPowerDependency()
    {
        foreach (var file in CoreUnattendedSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("IPowerService", content);
            Assert.DoesNotContain("PowerRequest", content);
            Assert.DoesNotContain("PowerResult", content);
            Assert.DoesNotContain("ExitWindowsEx", content);
            Assert.DoesNotContain("SetSuspendState", content);
            Assert.DoesNotContain("DllImport", content);
            Assert.DoesNotContain("shutdown.exe", content);
            Assert.DoesNotContain("Process.Start", content);
        }
    }

    [Fact]
    public void UnattendedCoreTypes_HaveNoRemoteOrNetworkWriteSurface()
    {
        foreach (var file in CoreUnattendedSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("HttpClient", content);
            Assert.DoesNotContain("Socket", content);
            Assert.DoesNotContain("NetworkStream", content);
            Assert.DoesNotContain("TcpClient", content);
        }
    }

    [Fact]
    public void UnattendedPolicyService_ExposesReadOnlyEvaluation_PlusLocalEnableRevoke()
    {
        var service = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Unattended", "UnattendedPolicyService.cs"));
        var store = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Unattended", "UnattendedAuthorizationStore.cs"));

        // 评估为只读决策；启用/撤销为本地原子写入（经 IStorage，无直接文件/网络句柄）。
        Assert.Contains("EvaluateAsync", service);
        Assert.Contains("EnableAsync", service);
        Assert.Contains("RevokeAsync", service);

        // 原子写入证据：经 IStorage.WriteAsync，绝不直接 File.WriteAllText。
        Assert.Contains("WriteAsync", store);
        Assert.DoesNotContain("File.WriteAllText", store);
        Assert.DoesNotContain("File.Delete", store);
    }

    [Fact]
    public void UnattendedAuthorizationStore_ReusesAtomicStorage_AndDistinguishesCorruptFromNotFound()
    {
        var store = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Unattended", "UnattendedAuthorizationStore.cs"));

        // 复用 IStorage 原子写入（临时文件 + 替换 + 备份）与损坏检测语义。
        Assert.Contains("IStorage", store);
        Assert.Contains("StorageReadStatus.Corrupt", store);
        Assert.Contains("StorageReadStatus.NotFound", store);
        Assert.Contains("UnattendedLoadStatus.UnsupportedVersion", store);
    }

    [Fact]
    public void UnattendedEvaluator_IsPureAndEncodesCancelWinsAndFailClosed()
    {
        var evaluator = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Unattended", "UnattendedConfirmationEvaluator.cs"));

        Assert.Contains("TaskInstanceState.Cancelled", evaluator);
        Assert.Contains("TaskInstanceState.Interrupted", evaluator);
        Assert.Contains("TaskInstanceState.Executed", evaluator);
        Assert.Contains("IsIdleRecovered", evaluator);
        Assert.Contains("!authorization.IsAuthorized", evaluator);
    }

    private static IEnumerable<string> CoreUnattendedSources()
        => Directory.GetFiles(Path.Combine(CoreSourceRoot(), "Unattended"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(CoreSourceRoot(), "Abstractions"), "IUnattendedPolicyService.cs"));

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
