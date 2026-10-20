using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.App.Infrastructure.Appearance;

/// <summary>appearance.json 文档：只保存外观选择，与 config.json（电源安全配置）分开存放。</summary>
public sealed class AppearanceSettingsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? SkinId { get; init; }
}

public enum AppearanceSettingsLoadStatus
{
    Success = 0,
    NotFound = 1,
    Corrupt = 2,
    IoFailure = 3,
    UnsupportedVersion = 4
}

public sealed record AppearanceSettingsLoadResult(AppearanceSettingsLoadStatus Status, string? SkinId = null, string? Error = null);

/// <summary>
/// appearance.json 读写：经 <see cref="IStorage"/> 复用原子写入与备份。任何读取失败都只影响外观，
/// 调用方回退到默认皮肤即可，绝不阻止启动。
/// </summary>
public sealed class AppearanceSettingsStore
{
    public const string FileName = "appearance.json";

    private readonly IStorage _storage;

    public AppearanceSettingsStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<AppearanceSettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<AppearanceSettingsDocument>(FileName, cancellationToken).ConfigureAwait(false);
        return read.Status switch
        {
            StorageReadStatus.Success when read.Value!.SchemaVersion != AppearanceSettingsDocument.CurrentSchemaVersion
                => new(AppearanceSettingsLoadStatus.UnsupportedVersion, Error: "SchemaVersion=" + read.Value.SchemaVersion),
            StorageReadStatus.Success => new(AppearanceSettingsLoadStatus.Success, read.Value!.SkinId),
            StorageReadStatus.NotFound => new(AppearanceSettingsLoadStatus.NotFound),
            StorageReadStatus.Corrupt => new(AppearanceSettingsLoadStatus.Corrupt, Error: read.Error),
            _ => new(AppearanceSettingsLoadStatus.IoFailure, Error: read.Error)
        };
    }

    public async Task<bool> SaveAsync(string skinId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skinId);
        var result = await _storage.WriteAsync(
            FileName,
            new AppearanceSettingsDocument { SkinId = skinId },
            cancellationToken).ConfigureAwait(false);
        return result.Status == StorageWriteStatus.Success;
    }
}
