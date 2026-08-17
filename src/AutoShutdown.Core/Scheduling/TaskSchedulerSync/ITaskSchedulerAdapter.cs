namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

/// <summary>外部任务（Windows 任务计划程序）查询结果状态。</summary>
public enum ExternalTaskQueryStatus
{
    Unknown = 0,
    Success = 1,

    /// <summary>权限被拒（例如计划程序注册表/COM 拒绝访问）。UI 应给出修复建议。</summary>
    PermissionDenied = 2,

    IoFailure = 3
}

/// <summary>外部任务增删改结果状态。</summary>
public enum ExternalTaskMutationStatus
{
    Unknown = 0,
    Success = 1,

    /// <summary>目标任务不存在（删除/更新时）。</summary>
    NotFound = 2,

    /// <summary>权限被拒（例如无管理员权限创建/修改任务）。</summary>
    PermissionDenied = 3,

    /// <summary>规格本身无效（例如名字或动作非法）。</summary>
    Invalid = 4,

    IoFailure = 5
}

public sealed record ExternalTaskQueryResult
{
    public ExternalTaskQueryStatus Status { get; init; } = ExternalTaskQueryStatus.Unknown;

    /// <summary>仅当 Status==Success 时有效：本应用拥有的全部外部任务。</summary>
    public IReadOnlyList<ExternalTaskState> Tasks { get; init; } = [];

    public string? Message { get; init; }
}

public sealed record ExternalTaskMutationResult
{
    public ExternalTaskMutationStatus Status { get; init; } = ExternalTaskMutationStatus.Unknown;

    public string? Message { get; init; }

    public bool Succeeded => Status == ExternalTaskMutationStatus.Success;
}

/// <summary>
/// 平台适配器边界：把纯同步引擎（<see cref="TaskSyncService"/>）与具体任务计划程序实现解耦。
/// 只允许外发（outbound）操作——查询/创建/更新/删除外部任务；绝不把外部状态写回本地事实源。
/// 实现位于 App 层（CP4 的 WinTaskSchedulerAdapter），使用 TaskScheduler 2.12.2 包。
/// 约定：QueryOwnedAsync 只返回「专属目录 + 应用标识 + 稳定本地 id」三重条件匹配的自家任务；
/// 同步引擎仍会按 <see cref="TaskSyncNaming.TryParseOwnedTaskName"/> 二次防御，绝不触碰他应用任务。
/// </summary>
public interface ITaskSchedulerAdapter
{
    /// <summary>查询本应用拥有的全部外部任务。</summary>
    Task<ExternalTaskQueryResult> QueryOwnedAsync(CancellationToken cancellationToken);

    /// <summary>按规格创建外部任务（名字取自 <see cref="ExternalTaskSpec.Name"/>）。</summary>
    Task<ExternalTaskMutationResult> CreateAsync(ExternalTaskSpec spec, CancellationToken cancellationToken);

    /// <summary>按规格更新既有外部任务（名字取自 <see cref="ExternalTaskSpec.Name"/>）。</summary>
    Task<ExternalTaskMutationResult> UpdateAsync(ExternalTaskSpec spec, CancellationToken cancellationToken);

    /// <summary>删除指定名字的外部任务（仅自家任务；调用方保证名字可被解析）。</summary>
    Task<ExternalTaskMutationResult> DeleteAsync(string taskName, CancellationToken cancellationToken);
}
