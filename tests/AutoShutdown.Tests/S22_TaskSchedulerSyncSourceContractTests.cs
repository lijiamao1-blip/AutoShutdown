using System.IO;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 源码契约测试。验证 Windows 任务计划程序单向同步的安全不变量：
/// 1) Core 同步层（TaskSchedulerSync）绝不新增电源出口：无 IPowerService（注释除外）、
///    无 P/Invoke、无 shutdown.exe、无进程启动；外部触发经本地调度引擎的唯一接入/仲裁路径
///    执行（ExternalTriggerTaskCommand + SubmitAsync），绝不直接调用 handler 或 Workflow。
/// 2) 绝不 inbound：同步层只读本地事实源（GetAll/LoadAsync），绝无写入本地 tasks.json 的
///    路径；外部任务永不反向覆盖本地。
/// 3) 外部动作只回调本地应用（--trigger-task &lt;id&gt;），适配器只清理「专属目录 + 应用标识 +
///    稳定本地 id」三重条件匹配的任务。
/// 4) 配置读取严格区分 NotFound/Corrupt/Invalid/UnsupportedVersion；损坏/非法 fail-closed。
/// </summary>
public sealed class S22_TaskSchedulerSyncSourceContractTests
{
    [Fact]
    public void CoreSyncTypes_HaveNoPowerExit()
    {
        foreach (var file in CoreSyncSources())
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotContain("ExitWindowsEx", content);
            Assert.DoesNotContain("SetSuspendState", content);
            Assert.DoesNotContain("DllImport", content);
            Assert.DoesNotContain("shutdown.exe", content);
            Assert.DoesNotContain("Process.Start", content);
            Assert.DoesNotContain("PowerRequest", content);
            Assert.DoesNotContain("PowerResult", content);

            // IPowerService 只在文档注释里声明「ShutdownWorkflow 是唯一调用方」这一不变量，
            // 代码（非注释行）绝不引用。
            Assert.DoesNotContain(
                CodeLines(content),
                line => line.Contains("IPowerService", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CoreSyncTypes_NeverWriteToLocalTaskStore()
    {
        foreach (var file in CoreSyncSources())
        {
            var content = File.ReadAllText(file);

            // 同步层只读本地事实源；任何 SaveAsync/WriteAsync 都构成 inbound 风险。
            Assert.DoesNotContain("SaveAsync", content);
            Assert.DoesNotContain("WriteAsync", content);
        }
    }

    [Fact]
    public void Coordinator_ReadsLocalOnly_ThroughGetAll()
    {
        var coordinator = File.ReadAllText(Path.Combine(SyncRoot(), "TaskSyncCoordinator.cs"));

        // 唯一本地数据入口是 ITaskService.GetAll()（只读快照），绝不调用任何本地写方法。
        Assert.Contains("GetAll", coordinator);
        Assert.Contains("CollectionChanged", coordinator);
        Assert.DoesNotContain("_taskService.Add", coordinator);
    }

    [Fact]
    public void ExternalTrigger_EncodesStrictTaxonomyAndRoutesViaEngine()
    {
        var service = File.ReadAllText(Path.Combine(SyncRoot(), "ExternalTaskTriggerService.cs"));

        // fail-closed 的状态机完整编码（行为已由 S22_ExternalTriggerTests 覆盖）。
        Assert.Contains("TaskNotFound", service);
        Assert.Contains("Disabled", service);
        Assert.Contains("GateRejected", service);
        Assert.Contains("ConfigLoadFailed", service);
        Assert.Contains("ExecutionFailed", service);
        Assert.Contains("Deduped", service);

        // 严格读取本地事实源：经 TasksDocumentStore.LoadAsync（区分 NotFound/Corrupt/Invalid/
        // UnsupportedVersion），绝不直接读文件。
        Assert.Contains("TasksDocumentStore", service);
        Assert.Contains("LoadAsync", service);

        // S22-D2：外部触发经本地调度引擎唯一接入/仲裁路径（ExternalTriggerTaskCommand +
        // SubmitAsync），绝不直接调用 handler 或 Workflow，也绝不直接执行电源。
        Assert.Contains("ISchedulerEngine", service);
        Assert.Contains("ExternalTriggerTaskCommand", service);
        Assert.Contains("SubmitAsync", service);
        Assert.DoesNotContain("IScheduledTaskHandler", service);
        Assert.DoesNotContain("HandleDueAsync", service);
    }

    [Fact]
    public void ScheduleGate_IsPureDecisionLayer()
    {
        var gate = File.ReadAllText(Path.Combine(SyncRoot(), "ExternalTriggerScheduleGate.cs"));

        Assert.Contains("INextExecutionCalculator", gate);
        Assert.Contains("OutsideWindow", gate);
        Assert.Contains("NoSchedule", gate);
        Assert.DoesNotContain("DllImport", gate);
    }

    [Fact]
    public void Naming_EncodesTripleConditionWithStableFormat()
    {
        var naming = File.ReadAllText(Path.Combine(SyncRoot(), "TaskSyncNaming.cs"));

        // 专属目录 + 应用标识 + 稳定本地 id：三条件中的后两项在此编码。
        Assert.Contains("DedicatedFolderPath = @\"AutoShutdown V2\"", naming);
        Assert.Contains("AppIdentifier = \"AutoShutdownV2\"", naming);
        Assert.Contains("BuildTaskName", naming);
        Assert.Contains("TryParseOwnedTaskName", naming);

        // 只允许本模块构建名称；构建后回验（fail-closed）。
        Assert.Contains("AppIdentifier + Separator", naming);
        Assert.Contains("does not round-trip", naming);
    }

    [Fact]
    public void SettingsStore_DistinguishesNotFoundFromCorrupt()
    {
        var store = File.ReadAllText(Path.Combine(CoreSourceRoot(), "Storage", "TaskSyncSettingsStore.cs"));

        Assert.Contains("StorageReadStatus.NotFound", store);
        Assert.Contains("StorageReadStatus.Corrupt", store);
        Assert.Contains("StorageReadStatus.IoFailure", store);
        Assert.Contains("TaskSyncSettingsLoadStatus.UnsupportedVersion", store);
    }

    [Fact]
    public void WinAdapter_OnlyCallbacksLocalApp_AndTripleConditionCleanup()
    {
        var adapter = File.ReadAllText(Path.Combine(
            AppSourceRoot(),
            "Infrastructure",
            "TaskScheduler",
            "WinTaskSchedulerAdapter.cs"));

        // 外部动作只回调本地应用（--trigger-task <id>），绝不携带电源命令。
        Assert.Contains("--trigger-task", adapter);
        Assert.Contains("ExecAction", adapter);
        Assert.DoesNotContain("shutdown.exe", adapter);
        Assert.DoesNotContain("SetSuspendState", adapter);
        Assert.DoesNotContain("ExitWindowsEx", adapter);

        // 清理前防御性二次校验：只删除/更新/创建名字可解析为自家任务的外部任务。
        Assert.Contains("TryParseOwnedTaskName", adapter);
        Assert.Contains("Refusing to delete a task that is not owned by this application.", adapter);
    }

    private static IEnumerable<string> CoreSyncSources()
        => Directory.GetFiles(SyncRoot(), "*.cs");

    private static string SyncRoot()
        => Path.Combine(CoreSourceRoot(), "Scheduling", "TaskSchedulerSync");

    private static string CoreSourceRoot() => FindRoot("AutoShutdown.Core");

    private static string AppSourceRoot() => FindRoot("AutoShutdown.App");

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

    /// <summary>只取代码行（剔除 ///、//、/* 注释），用于"代码不得引用电源"类断言。</summary>
    private static IEnumerable<string> CodeLines(string content)
        => content.Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("///", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("/*", StringComparison.Ordinal));
}
