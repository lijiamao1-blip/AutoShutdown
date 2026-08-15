namespace AutoShutdown.Core.Storage;

public enum StorageReadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4
}

public enum StorageWriteStatus
{
    Unknown = 0,
    Success = 1,
    SerializationFailure = 2,
    ValidationFailure = 3,
    IoFailure = 4
}

public sealed record StorageReadResult<T>
{
    public StorageReadStatus Status { get; init; } = StorageReadStatus.Unknown;
    public T? Value { get; init; }
    public string? Error { get; init; }
}

public sealed record StorageWriteResult
{
    public StorageWriteStatus Status { get; init; } = StorageWriteStatus.Unknown;
    public string? BackupPath { get; init; }
    public string? Error { get; init; }

    public bool Succeeded => Status == StorageWriteStatus.Success;
}
