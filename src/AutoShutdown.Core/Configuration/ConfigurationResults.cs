namespace AutoShutdown.Core.Configuration;

public enum ConfigurationLoadStatus
{
    Unknown = 0,
    Success = 1,
    Missing = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6,
    MigrationUnavailable = 7
}

public sealed record ConfigurationLoadResult
{
    public ConfigurationLoadStatus Status { get; init; } = ConfigurationLoadStatus.Unknown;

    public AppConfig? Config { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public enum ConfigurationSaveStatus
{
    Unknown = 0,
    Success = 1,
    Invalid = 2,
    IoFailure = 3
}

public sealed record ConfigurationSaveResult
{
    public ConfigurationSaveStatus Status { get; init; } = ConfigurationSaveStatus.Unknown;

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Succeeded => Status == ConfigurationSaveStatus.Success;
}
