namespace AutoShutdown.Core.WakeOnLan;

/// <summary>目标机器增删改操作的结果状态（S21）。</summary>
public enum TargetMachineMutationStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    DuplicateId = 3,
    Invalid = 4,

    /// <summary>当前文档无法载入（缺失之外的一切状态：损坏/非法/版本不支持/IO），拒绝写入。</summary>
    LoadUnavailable = 5,
    IoFailure = 6
}

public sealed record TargetMachineMutationResult
{
    public TargetMachineMutationStatus Status { get; init; } = TargetMachineMutationStatus.Unknown;

    public TargetMachinesDocument? Document { get; init; }

    public string? Error { get; init; }

    public bool Succeeded => Status == TargetMachineMutationStatus.Success;
}
