namespace AutoShutdown.Core.Tasks;

/// <summary>任务定义集合操作的返回状态。</summary>
public enum TaskCollectionStatus
{
    Unknown = 0,
    Success = 1,
    InvalidDefinition = 2,
    DuplicateId = 3,
    NotFound = 4
}

/// <summary>任务定义集合操作的结构化结果。</summary>
public sealed record TaskCollectionResult
{
    public TaskCollectionStatus Status { get; init; } = TaskCollectionStatus.Unknown;

    /// <summary>操作涉及的任务定义（Add/Update 成功时为落库后的定义；Get 命中时返回）。</summary>
    public TaskDefinition? Definition { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Status == TaskCollectionStatus.Success;
}
