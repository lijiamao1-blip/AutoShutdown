namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

/// <summary>
/// Windows Task Scheduler 外部任务的稳定命名契约（S22 单向同步）。
/// 名称 = 应用标识 + 稳定本地 task id，三者合一提供「专属目录 + 应用标识 + 稳定 id」
/// 清理三重条件中的后两项（目录条件由适配器固定在专属目录）。
/// 只允许本模块构建名称；外部任务名与本地 id 的往返解析是「只清理本应用拥有的任务」
/// 的唯一依据——解析失败即视为非本应用任务，绝不删除。
/// </summary>
public static class TaskSyncNaming
{
    /// <summary>外部任务所在专属目录（Windows 任务计划程序根目录下的相对路径，不含盘符）。</summary>
    public const string DedicatedFolderPath = @"AutoShutdown V2";

    /// <summary>外部任务名的应用标识前缀（与本地 task id 之间以 -- 分隔）。</summary>
    public const string AppIdentifier = "AutoShutdownV2";

    // Windows Task Scheduler 的任务名最终受 Windows 文件名规则约束；':' 会使
    // RegisterTaskDefinition 返回 E_INVALIDARG。仅使用合法的普通连字符。
    private const string Separator = "--";

    /// <summary>稳定任务名：AutoShutdownV2--&lt;taskId:D&gt;。名称仅含字母、数字和连字符，
    /// 不含 Windows 任务名禁止字符；构建后仍做一次解析回验（fail-closed）。</summary>
    public static string BuildTaskName(Guid taskId)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("taskId must not be an empty GUID.", nameof(taskId));
        }

        var name = AppIdentifier + Separator + taskId.ToString("D");

        // 回验：构建出的名称必须能被本模块解析回同一个 id；失败视为契约破坏。
        if (!TryParseOwnedTaskName(name, out var parsed) || parsed != taskId)
        {
            throw new InvalidOperationException("The built task name does not round-trip through the naming contract.");
        }

        return name;
    }

    /// <summary>
    /// 解析外部任务名是否为「本应用拥有」。必须同时满足：应用标识前缀 + 稳定本地 task id
    /// （非空 Guid）。任何一项不满足都返回 false——该任务绝不在清理范围内。
    /// </summary>
    public static bool TryParseOwnedTaskName(string? name, out Guid taskId)
    {
        taskId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var expectedPrefix = AppIdentifier + Separator;
        if (!name.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var idText = name[expectedPrefix.Length..];
        return Guid.TryParseExact(idText, "D", out taskId) && taskId != Guid.Empty;
    }
}
