namespace AutoShutdown.Core.Tasks;

/// <summary>任务定义集合的变更种类（S22 CP3 事件接入）。</summary>
public enum TaskCollectionChangeKind
{
    Unknown = 0,
    Added = 1,
    Updated = 2,
    Removed = 3,

    /// <summary>分别启用（SetEnabled true）。</summary>
    Enabled = 4,

    /// <summary>分别禁用（SetEnabled false）。</summary>
    Disabled = 5
}

/// <summary>
/// 任务集合变更事件参数（S22 CP3）：仅在集合变更成功提交后同步触发（变更线程）。
/// 供 outbound 同步协调器消费；绝不携带任何「外部状态」信息，杜绝 inbound 回写通道。
/// </summary>
public sealed class TaskCollectionChangedEventArgs : EventArgs
{
    public TaskCollectionChangeKind Kind { get; init; } = TaskCollectionChangeKind.Unknown;

    public Guid TaskId { get; init; }

    /// <summary>变更后的任务定义（Removed 为 null；其他情况为落库后的最新定义）。</summary>
    public TaskDefinition? Definition { get; init; }
}
