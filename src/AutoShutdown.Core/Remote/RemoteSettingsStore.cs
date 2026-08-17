using System.Net;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Remote;

/// <summary>remote-settings.json 文档（S23）：远程控制开关、监听地址/端口、TLS 要求与远程命令白名单。</summary>
public sealed class RemoteSettingsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>远程控制总开关。默认关闭（绝不静默启用）。</summary>
    public bool Enabled { get; init; }

    /// <summary>监听地址。默认 127.0.0.1（仅本机）；局域网需改为本机局域网地址或 0.0.0.0。</summary>
    public string ListenAddress { get; init; } = "127.0.0.1";

    /// <summary>监听端口。默认 48620。</summary>
    public int ListenPort { get; init; } = 48620;

    /// <summary>是否强制 TLS。默认 true。false 时允许明文连接，但配对与无人值守等效确认仍必须 TLS。</summary>
    public bool RequireTls { get; init; } = true;

    /// <summary>是否要求客户端出示 TLS 客户端证书（可选项）。默认 false；应用层 HMAC 为主鉴权。</summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>证书模式：true = 使用导入证书（ImportedCertPath）；false = 自签名自动生成。</summary>
    public bool UseImportedCertificate { get; init; }

    /// <summary>导入证书 PFX 路径（UseImportedCertificate=true 时使用）。</summary>
    public string? ImportedCertPath { get; init; }

    /// <summary>远程命令白名单（独立于 S19 本地白名单；默认仅只读）。</summary>
    public RemoteCommandWhiteList WhiteList { get; init; } = new();
}

public enum RemoteSettingsLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6
}

public sealed record RemoteSettingsLoadResult
{
    public RemoteSettingsLoadStatus Status { get; init; } = RemoteSettingsLoadStatus.Unknown;

    public RemoteSettingsDocument? Document { get; init; }

    public string? Error { get; init; }
}

public sealed record RemoteSettingsSaveResult
{
    public bool Succeeded { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// remote-settings.json 文档封装（S23 CP2）：通过 <see cref="IStorage"/> 复用原子写入与备份。
/// 读取严格区分 NotFound/Corrupt/Invalid/UnsupportedVersion/IoFailure；损坏/非法绝不静默回退为
/// 「启用远程」——默认安全值是关闭（Enabled=false，绝不监听）。远程请求永远不写本文件。
/// </summary>
public sealed class RemoteSettingsStore
{
    public const string FileName = "remote-settings.json";

    private readonly IStorage _storage;

    public RemoteSettingsStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<RemoteSettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return new RemoteSettingsLoadResult { Status = RemoteSettingsLoadStatus.NotFound };
            case StorageReadStatus.Corrupt:
                return new RemoteSettingsLoadResult
                {
                    Status = RemoteSettingsLoadStatus.Corrupt,
                    Error = read.Error
                };
            case StorageReadStatus.IoFailure:
                return new RemoteSettingsLoadResult
                {
                    Status = RemoteSettingsLoadStatus.IoFailure,
                    Error = read.Error
                };
            case StorageReadStatus.Success:
                break;
            default:
                return new RemoteSettingsLoadResult
                {
                    Status = RemoteSettingsLoadStatus.IoFailure,
                    Error = "The storage layer returned an unknown status."
                };
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new RemoteSettingsLoadResult
            {
                Status = RemoteSettingsLoadStatus.Invalid,
                Error = "The remote-settings root must be a JSON object."
            };
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return new RemoteSettingsLoadResult
            {
                Status = RemoteSettingsLoadStatus.Invalid,
                Error = "SchemaVersion must be an integer."
            };
        }

        if (version != RemoteSettingsDocument.CurrentSchemaVersion)
        {
            return new RemoteSettingsLoadResult
            {
                Status = version > RemoteSettingsDocument.CurrentSchemaVersion
                    ? RemoteSettingsLoadStatus.UnsupportedVersion
                    : RemoteSettingsLoadStatus.Invalid,
                Error = $"SchemaVersion {version} is not supported; expected {RemoteSettingsDocument.CurrentSchemaVersion}."
            };
        }

        RemoteSettingsDocument? document;
        try
        {
            document = root.Deserialize<RemoteSettingsDocument>();
        }
        catch (JsonException exception)
        {
            return new RemoteSettingsLoadResult
            {
                Status = RemoteSettingsLoadStatus.Invalid,
                Error = "The remote-settings document could not be deserialized: " + exception.Message
            };
        }

        if (document is null)
        {
            return new RemoteSettingsLoadResult
            {
                Status = RemoteSettingsLoadStatus.Invalid,
                Error = "The remote-settings document contains no value."
            };
        }

        var validationErrors = Validate(document);
        if (validationErrors.Count > 0)
        {
            return new RemoteSettingsLoadResult
            {
                Status = RemoteSettingsLoadStatus.Invalid,
                Error = string.Join(" ", validationErrors)
            };
        }

        return new RemoteSettingsLoadResult
        {
            Status = RemoteSettingsLoadStatus.Success,
            Document = document
        };
    }

    public async Task<RemoteSettingsSaveResult> SaveAsync(
        RemoteSettingsDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion != RemoteSettingsDocument.CurrentSchemaVersion)
        {
            return new RemoteSettingsSaveResult
            {
                Succeeded = false,
                Error = $"SchemaVersion must be {RemoteSettingsDocument.CurrentSchemaVersion}."
            };
        }

        var validationErrors = Validate(document);
        if (validationErrors.Count > 0)
        {
            return new RemoteSettingsSaveResult
            {
                Succeeded = false,
                Error = string.Join(" ", validationErrors)
            };
        }

        var write = await _storage.WriteAsync(FileName, document, cancellationToken);
        return write.Status == StorageWriteStatus.Success
            ? new RemoteSettingsSaveResult { Succeeded = true }
            : new RemoteSettingsSaveResult
            {
                Succeeded = false,
                Error = write.Error ?? "The remote-settings document could not be written."
            };
    }

    /// <summary>结构校验：端口范围、监听地址可解析、白名单合法、导入证书路径非空。</summary>
    public static IReadOnlyList<string> Validate(RemoteSettingsDocument? document)
    {
        if (document is null)
        {
            return ["Remote settings must not be null."];
        }

        var errors = new List<string>();

        if (document.ListenPort is < 1 or > 65535)
        {
            errors.Add($"ListenPort must be between 1 and 65535, but was {document.ListenPort}.");
        }

        if (!TryParseListenAddress(document.ListenAddress))
        {
            errors.Add($"ListenAddress '{document.ListenAddress}' is not a valid IP address.");
        }

        if (document.UseImportedCertificate && string.IsNullOrWhiteSpace(document.ImportedCertPath))
        {
            errors.Add("ImportedCertPath must be set when UseImportedCertificate is true.");
        }

        errors.AddRange(RemoteCommandWhiteListValidator.Validate(document.WhiteList));

        return errors;
    }

    private static bool TryParseListenAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        return IPAddress.TryParse(address.Trim(), out _);
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
