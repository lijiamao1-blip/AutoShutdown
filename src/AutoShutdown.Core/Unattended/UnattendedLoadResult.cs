namespace AutoShutdown.Core.Unattended;

/// <summary>无人值守授权记录的载入状态。</summary>
public enum UnattendedLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6
}

public sealed record UnattendedLoadResult
{
    public UnattendedLoadStatus Status { get; init; } = UnattendedLoadStatus.Unknown;

    public UnattendedPolicy? Policy { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}
