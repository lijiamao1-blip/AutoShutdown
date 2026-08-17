namespace AutoShutdown.Core.WakeOnLan;

/// <summary>目标机器文档载入状态（S21）。严格区分 NotFound / Corrupt / Invalid / UnsupportedVersion。</summary>
public enum TargetMachinesLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6
}

public sealed record TargetMachinesLoadResult
{
    public TargetMachinesLoadStatus Status { get; init; } = TargetMachinesLoadStatus.Unknown;

    public TargetMachinesDocument? Document { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Succeeded => Status == TargetMachinesLoadStatus.Success;
}
