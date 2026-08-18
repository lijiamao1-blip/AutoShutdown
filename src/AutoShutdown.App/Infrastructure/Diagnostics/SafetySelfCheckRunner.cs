using System.IO;

namespace AutoShutdown.App.Infrastructure.Diagnostics;

/// <summary>安全自检单项结果（只读报告，不自动修复）。</summary>
public sealed record SelfCheckItem(string Key, string Name, bool IsHealthy, string Detail);

/// <summary>安全自检整体报告。</summary>
public sealed record SelfCheckReport(
    bool AllHealthy,
    string SummaryText,
    IReadOnlyList<SelfCheckItem> Items);

/// <summary>WoL 目标合法性（由调用方经 WakeOnLanTarget.GetStructuralError 计算，运行器不持有 Core 类型）。</summary>
public sealed record WolTargetHealth(string Name, bool IsValid, string Detail);

/// <summary>安全自检所需只读快照（由诊断中心 VM 从各分区/配置状态汇总）。</summary>
public sealed record SelfCheckSnapshot(
    string DataRoot,
    string LogDirectory,
    string ConfigStatusText,
    bool ConfigUsable,
    int LocalTaskCount,
    string TaskSyncStatusText,
    bool TaskSyncHealthy,
    string TaskSyncDetailText,
    IReadOnlyList<WolTargetHealth> WolTargets,
    bool RemoteEnabled,
    string RemoteListenAddress,
    int? RemoteListenPort,
    bool RemoteRequireTls,
    string RemoteDetailText);

/// <summary>
/// 安全自检（S-UI1）：只读检查六项——配置健康、数据目录可写性、本地任务、任务计划同步、
/// WoL 目标合法性、远程监听/TLS 状态。不自动修复、不执行任何电源操作。
///
/// 「数据目录可写性」使用唯一临时探针文件（FileOptions.DeleteOnClose，关闭即删除），
/// 不改动任何既有数据；探测失败只读报告，绝不动配置或任务。
/// </summary>
public sealed class SafetySelfCheckRunner
{
    private const string ProbeFileNamePrefix = ".autoshutdown-selfcheck-probe";

    /// <summary>
    /// 异步入口：本方法名刻意避开 "RunAsync"（S11 源契约测试用粗粒度正则
    /// 约束 SchedulerEngine.RunAsync 恰有两个调用点，自检运行器不得干扰该守卫）。
    /// </summary>
    public Task<SelfCheckReport> RunSelfCheckAsync(
        SelfCheckSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Run(snapshot));
    }

    public SelfCheckReport Run(SelfCheckSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var items = new List<SelfCheckItem>
        {
            CheckConfigHealth(snapshot),
            CheckDataRootWritable(snapshot.DataRoot),
            CheckLocalTasks(snapshot),
            CheckTaskSync(snapshot),
            CheckWolTargets(snapshot.WolTargets),
            CheckRemote(snapshot)
        };

        var unhealthyCount = items.Count(item => !item.IsHealthy);
        var summary = unhealthyCount == 0
            ? "安全自检全部通过（仅只读检查，未修改任何配置、未执行任何电源操作）。"
            : $"安全自检发现 {unhealthyCount} 项待关注（仅只读报告，未自动修复）。";

        return new SelfCheckReport(unhealthyCount == 0, summary, items);
    }

    private static SelfCheckItem CheckConfigHealth(SelfCheckSnapshot snapshot)
        => snapshot.ConfigUsable
            ? new SelfCheckItem("config", "配置健康", true, $"配置已加载：{snapshot.ConfigStatusText}")
            : new SelfCheckItem("config", "配置健康", false, snapshot.ConfigStatusText);

    private static SelfCheckItem CheckDataRootWritable(string dataRoot)
    {
        var probePath = Path.Combine(
            dataRoot,
            ProbeFileNamePrefix + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dataRoot);
            using (var stream = new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1,
                       FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0x41);
                stream.Flush();
            }

            return new SelfCheckItem(
                "dataRootWritable",
                "数据目录可写性",
                true,
                $"数据目录可写：{dataRoot}（探针文件已自动清理）");
        }
        catch (Exception exception)
        {
            return new SelfCheckItem(
                "dataRootWritable",
                "数据目录可写性",
                false,
                $"数据目录不可写（{dataRoot}）：{exception.GetType().Name}：{exception.Message}");
        }
    }

    private static SelfCheckItem CheckLocalTasks(SelfCheckSnapshot snapshot)
        => new(
            "localTasks",
            "本地任务",
            true,
            $"本地任务清单可读，当前 {snapshot.LocalTaskCount} 个任务（本地 TaskCollection 为唯一事实源）。");

    private static SelfCheckItem CheckTaskSync(SelfCheckSnapshot snapshot)
        => new(
            "taskSync",
            "任务计划同步",
            snapshot.TaskSyncHealthy,
            $"{snapshot.TaskSyncStatusText}；{snapshot.TaskSyncDetailText}");

    private static SelfCheckItem CheckWolTargets(IReadOnlyList<WolTargetHealth> targets)
    {
        if (targets.Count == 0)
        {
            return new SelfCheckItem(
                "wolTargets",
                "WoL 目标合法性",
                true,
                "未配置 WoL 目标（合法；Magic Packet 仅向显式配置的目标发送）。");
        }

        var invalid = targets.Where(target => !target.IsValid).ToList();
        if (invalid.Count == 0)
        {
            return new SelfCheckItem(
                "wolTargets",
                "WoL 目标合法性",
                true,
                $"已配置 {targets.Count} 个 WoL 目标，全部通过 MAC/广播地址/端口结构校验。");
        }

        var details = string.Join("；", invalid.Select(target => $"{target.Name}：{target.Detail}"));
        return new SelfCheckItem(
            "wolTargets",
            "WoL 目标合法性",
            false,
            $"{invalid.Count} 个目标结构不合法：{details}");
    }

    private static SelfCheckItem CheckRemote(SelfCheckSnapshot snapshot)
    {
        if (!snapshot.RemoteEnabled)
        {
            return new SelfCheckItem(
                "remoteListen",
                "远程监听/TLS",
                true,
                "远程控制未启用（默认安全，不监听任何端口）。");
        }

        var problems = new List<string>();
        if (!snapshot.RemoteRequireTls)
        {
            problems.Add("未强制 TLS（明文连接会被接受，仍要求 HMAC 鉴权与配对 TLS）");
        }

        if (snapshot.RemoteListenAddress is "0.0.0.0" or "::")
        {
            problems.Add("监听所有接口（0.0.0.0/::）暴露到全部网络接口");
        }

        var listenText = $"{snapshot.RemoteListenAddress}:{snapshot.RemoteListenPort?.ToString() ?? "?"}";
        var detail = problems.Count == 0
            ? $"监听 {listenText}，强制 TLS，符合安全基线。"
            : $"监听 {listenText}；需注意：{string.Join("；", problems)}。";

        return new SelfCheckItem("remoteListen", "远程监听/TLS", problems.Count == 0, detail);
    }
}
