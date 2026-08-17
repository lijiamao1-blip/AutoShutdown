using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Remote;

/// <summary>remote-devices.json 文档（S23）：已配对设备清单。sharedSecret 只以受保护（DPAPI）形态落盘。</summary>
public sealed class RemoteDevicesDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public PairedDevice[] Devices { get; init; } = [];
}

/// <summary>单个已配对设备。ProtectedSecretBase64 是 DPAPI 封装的共享密钥，绝不写明文。</summary>
public sealed record PairedDevice
{
    /// <summary>稳定设备 id（配对时生成并交给客户端）。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>设备名（客户端在配对请求中提供，仅审计/UI 展示）。</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>DPAPI 封装后的 sharedSecret（base64）。绝不含明文 secret。</summary>
    public string ProtectedSecretBase64 { get; init; } = string.Empty;

    public DateTimeOffset PairedAtUtc { get; init; }

    public DateTimeOffset LastSeenAtUtc { get; init; }
}

public enum RemoteDevicesLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6
}

public sealed record RemoteDevicesLoadResult
{
    public RemoteDevicesLoadStatus Status { get; init; } = RemoteDevicesLoadStatus.Unknown;

    public RemoteDevicesDocument? Document { get; init; }

    public string? Error { get; init; }
}

public sealed record RemoteDevicesSaveResult
{
    public bool Succeeded { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// remote-devices.json 文档封装（S23 CP2）：复用原子写入。读取严格区分
/// NotFound/Corrupt/Invalid/UnsupportedVersion/IoFailure；损坏/非法 fail-closed（不鉴权任何设备）。
/// </summary>
public sealed class RemoteDevicesStore
{
    public const string FileName = "remote-devices.json";

    private readonly IStorage _storage;

    public RemoteDevicesStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<RemoteDevicesLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return new RemoteDevicesLoadResult { Status = RemoteDevicesLoadStatus.NotFound };
            case StorageReadStatus.Corrupt:
                return new RemoteDevicesLoadResult
                {
                    Status = RemoteDevicesLoadStatus.Corrupt,
                    Error = read.Error
                };
            case StorageReadStatus.IoFailure:
                return new RemoteDevicesLoadResult
                {
                    Status = RemoteDevicesLoadStatus.IoFailure,
                    Error = read.Error
                };
            case StorageReadStatus.Success:
                break;
            default:
                return new RemoteDevicesLoadResult
                {
                    Status = RemoteDevicesLoadStatus.IoFailure,
                    Error = "The storage layer returned an unknown status."
                };
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new RemoteDevicesLoadResult
            {
                Status = RemoteDevicesLoadStatus.Invalid,
                Error = "The remote-devices root must be a JSON object."
            };
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return new RemoteDevicesLoadResult
            {
                Status = RemoteDevicesLoadStatus.Invalid,
                Error = "SchemaVersion must be an integer."
            };
        }

        if (version != RemoteDevicesDocument.CurrentSchemaVersion)
        {
            return new RemoteDevicesLoadResult
            {
                Status = version > RemoteDevicesDocument.CurrentSchemaVersion
                    ? RemoteDevicesLoadStatus.UnsupportedVersion
                    : RemoteDevicesLoadStatus.Invalid,
                Error = $"SchemaVersion {version} is not supported; expected {RemoteDevicesDocument.CurrentSchemaVersion}."
            };
        }

        RemoteDevicesDocument? document;
        try
        {
            document = root.Deserialize<RemoteDevicesDocument>();
        }
        catch (JsonException exception)
        {
            return new RemoteDevicesLoadResult
            {
                Status = RemoteDevicesLoadStatus.Invalid,
                Error = "The remote-devices document could not be deserialized: " + exception.Message
            };
        }

        if (document is null)
        {
            return new RemoteDevicesLoadResult
            {
                Status = RemoteDevicesLoadStatus.Invalid,
                Error = "The remote-devices document contains no value."
            };
        }

        foreach (var device in document.Devices)
        {
            if (device is null
                || string.IsNullOrWhiteSpace(device.DeviceId)
                || string.IsNullOrWhiteSpace(device.ProtectedSecretBase64))
            {
                return new RemoteDevicesLoadResult
                {
                    Status = RemoteDevicesLoadStatus.Invalid,
                    Error = "A device entry must have a non-empty DeviceId and ProtectedSecretBase64."
                };
            }
        }

        return new RemoteDevicesLoadResult
        {
            Status = RemoteDevicesLoadStatus.Success,
            Document = document
        };
    }

    public async Task<RemoteDevicesSaveResult> SaveAsync(
        RemoteDevicesDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion != RemoteDevicesDocument.CurrentSchemaVersion)
        {
            return new RemoteDevicesSaveResult
            {
                Succeeded = false,
                Error = $"SchemaVersion must be {RemoteDevicesDocument.CurrentSchemaVersion}."
            };
        }

        var write = await _storage.WriteAsync(FileName, document, cancellationToken);
        return write.Status == StorageWriteStatus.Success
            ? new RemoteDevicesSaveResult { Succeeded = true }
            : new RemoteDevicesSaveResult
            {
                Succeeded = false,
                Error = write.Error ?? "The remote-devices document could not be written."
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
