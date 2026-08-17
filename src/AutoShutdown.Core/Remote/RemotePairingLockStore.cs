using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Remote;

/// <summary>remote-pairing-lock.json 文档（S23）：配对失败计数与锁定到期时间（持久化，重启不绕过）。</summary>
public sealed class RemotePairingLockDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>连续配对失败次数。</summary>
    public int FailedAttempts { get; init; }

    /// <summary>锁定到期 UTC 时间；null = 未锁定。</summary>
    public DateTimeOffset? LockedUntilUtc { get; init; }
}

public enum RemotePairingLockLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6
}

public sealed record RemotePairingLockLoadResult
{
    public RemotePairingLockLoadStatus Status { get; init; } = RemotePairingLockLoadStatus.Unknown;

    public RemotePairingLockDocument? Document { get; init; }

    public string? Error { get; init; }
}

public sealed record RemotePairingLockSaveResult
{
    public bool Succeeded { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// remote-pairing-lock.json 文档封装（S23 CP2）：配对失败计数与锁定持久化。读取严格区分
/// NotFound/Corrupt/Invalid/UnsupportedVersion/IoFailure；损坏/非法 fail-closed（视为锁定）。
/// NotFound 视为首次使用（0 失败、未锁定）。
/// </summary>
public sealed class RemotePairingLockStore
{
    public const string FileName = "remote-pairing-lock.json";

    private readonly IStorage _storage;

    public RemotePairingLockStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<RemotePairingLockLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return new RemotePairingLockLoadResult { Status = RemotePairingLockLoadStatus.NotFound };
            case StorageReadStatus.Corrupt:
                return new RemotePairingLockLoadResult
                {
                    Status = RemotePairingLockLoadStatus.Corrupt,
                    Error = read.Error
                };
            case StorageReadStatus.IoFailure:
                return new RemotePairingLockLoadResult
                {
                    Status = RemotePairingLockLoadStatus.IoFailure,
                    Error = read.Error
                };
            case StorageReadStatus.Success:
                break;
            default:
                return new RemotePairingLockLoadResult
                {
                    Status = RemotePairingLockLoadStatus.IoFailure,
                    Error = "The storage layer returned an unknown status."
                };
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new RemotePairingLockLoadResult
            {
                Status = RemotePairingLockLoadStatus.Invalid,
                Error = "The remote-pairing-lock root must be a JSON object."
            };
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return new RemotePairingLockLoadResult
            {
                Status = RemotePairingLockLoadStatus.Invalid,
                Error = "SchemaVersion must be an integer."
            };
        }

        if (version != RemotePairingLockDocument.CurrentSchemaVersion)
        {
            return new RemotePairingLockLoadResult
            {
                Status = version > RemotePairingLockDocument.CurrentSchemaVersion
                    ? RemotePairingLockLoadStatus.UnsupportedVersion
                    : RemotePairingLockLoadStatus.Invalid,
                Error = $"SchemaVersion {version} is not supported; expected {RemotePairingLockDocument.CurrentSchemaVersion}."
            };
        }

        RemotePairingLockDocument? document;
        try
        {
            document = root.Deserialize<RemotePairingLockDocument>();
        }
        catch (JsonException exception)
        {
            return new RemotePairingLockLoadResult
            {
                Status = RemotePairingLockLoadStatus.Invalid,
                Error = "The remote-pairing-lock document could not be deserialized: " + exception.Message
            };
        }

        if (document is null)
        {
            return new RemotePairingLockLoadResult
            {
                Status = RemotePairingLockLoadStatus.Invalid,
                Error = "The remote-pairing-lock document contains no value."
            };
        }

        return new RemotePairingLockLoadResult
        {
            Status = RemotePairingLockLoadStatus.Success,
            Document = document
        };
    }

    public async Task<RemotePairingLockSaveResult> SaveAsync(
        RemotePairingLockDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion != RemotePairingLockDocument.CurrentSchemaVersion)
        {
            return new RemotePairingLockSaveResult
            {
                Succeeded = false,
                Error = $"SchemaVersion must be {RemotePairingLockDocument.CurrentSchemaVersion}."
            };
        }

        var write = await _storage.WriteAsync(FileName, document, cancellationToken);
        return write.Status == StorageWriteStatus.Success
            ? new RemotePairingLockSaveResult { Succeeded = true }
            : new RemotePairingLockSaveResult
            {
                Succeeded = false,
                Error = write.Error ?? "The remote-pairing-lock document could not be written."
            };
    }

    private static bool TryReadSchemaVersion(JsonElement root, out int version)
    {
        version = default;
        if (!root.TryGetProperty("SchemaVersion", out var versionElement))
        {
            return false;
        }

        if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out version))
        {
            return false;
        }

        return true;
    }
}
