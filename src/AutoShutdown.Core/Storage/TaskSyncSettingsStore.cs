using System.Text.Json;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Storage;

/// <summary>task-sync.json 文档（S22）：单向同步开关与最近同步时间。</summary>
public sealed class TaskSyncSettingsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>任务计划程序单向同步总开关。默认关闭（绝不静默启用）。</summary>
    public bool Enabled { get; init; }

    /// <summary>最近一次成功同步的 UTC 时间（审计用）。</summary>
    public DateTimeOffset? LastSyncAtUtc { get; init; }
}

public enum TaskSyncSettingsLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6
}

public sealed record TaskSyncSettingsLoadResult
{
    public TaskSyncSettingsLoadStatus Status { get; init; } = TaskSyncSettingsLoadStatus.Unknown;

    public TaskSyncSettingsDocument? Document { get; init; }

    public string? Error { get; init; }
}

public sealed record TaskSyncSettingsSaveResult
{
    public bool Succeeded { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// task-sync.json 文档封装（S22 CP4）：通过 <see cref="IStorage"/> 复用原子写入与备份证据。
/// 读取严格区分 NotFound（文件不存在）/ Corrupt（JSON 损坏）/ Invalid（结构非法）/
/// UnsupportedVersion（版本过高）/ IoFailure；损坏或非法绝不静默回退为「启用同步」——
/// 默认安全值是关闭同步（Enabled=false，绝不触发）。NotFound 视为首次使用（关闭）。
/// </summary>
public sealed class TaskSyncSettingsStore
{
    public const string FileName = "task-sync.json";

    private readonly IStorage _storage;

    public TaskSyncSettingsStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<TaskSyncSettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return new TaskSyncSettingsLoadResult { Status = TaskSyncSettingsLoadStatus.NotFound };
            case StorageReadStatus.Corrupt:
                return new TaskSyncSettingsLoadResult
                {
                    Status = TaskSyncSettingsLoadStatus.Corrupt,
                    Error = read.Error
                };
            case StorageReadStatus.IoFailure:
                return new TaskSyncSettingsLoadResult
                {
                    Status = TaskSyncSettingsLoadStatus.IoFailure,
                    Error = read.Error
                };
            case StorageReadStatus.Success:
                break;
            default:
                return new TaskSyncSettingsLoadResult
                {
                    Status = TaskSyncSettingsLoadStatus.IoFailure,
                    Error = "The storage layer returned an unknown status."
                };
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new TaskSyncSettingsLoadResult
            {
                Status = TaskSyncSettingsLoadStatus.Invalid,
                Error = "The task-sync document root must be a JSON object."
            };
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return new TaskSyncSettingsLoadResult
            {
                Status = TaskSyncSettingsLoadStatus.Invalid,
                Error = "SchemaVersion must be an integer."
            };
        }

        if (version != TaskSyncSettingsDocument.CurrentSchemaVersion)
        {
            return new TaskSyncSettingsLoadResult
            {
                Status = version > TaskSyncSettingsDocument.CurrentSchemaVersion
                    ? TaskSyncSettingsLoadStatus.UnsupportedVersion
                    : TaskSyncSettingsLoadStatus.Invalid,
                Error = $"SchemaVersion {version} is not supported; expected {TaskSyncSettingsDocument.CurrentSchemaVersion}."
            };
        }

        TaskSyncSettingsDocument? document;
        try
        {
            document = root.Deserialize<TaskSyncSettingsDocument>();
        }
        catch (JsonException exception)
        {
            return new TaskSyncSettingsLoadResult
            {
                Status = TaskSyncSettingsLoadStatus.Invalid,
                Error = "The task-sync document could not be deserialized: " + exception.Message
            };
        }

        if (document is null)
        {
            return new TaskSyncSettingsLoadResult
            {
                Status = TaskSyncSettingsLoadStatus.Invalid,
                Error = "The task-sync document contains no value."
            };
        }

        return new TaskSyncSettingsLoadResult
        {
            Status = TaskSyncSettingsLoadStatus.Success,
            Document = document
        };
    }

    public async Task<TaskSyncSettingsSaveResult> SaveAsync(
        TaskSyncSettingsDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion != TaskSyncSettingsDocument.CurrentSchemaVersion)
        {
            return new TaskSyncSettingsSaveResult
            {
                Succeeded = false,
                Error = $"SchemaVersion must be {TaskSyncSettingsDocument.CurrentSchemaVersion}."
            };
        }

        var write = await _storage.WriteAsync(FileName, document, cancellationToken);
        return write.Status == StorageWriteStatus.Success
            ? new TaskSyncSettingsSaveResult { Succeeded = true }
            : new TaskSyncSettingsSaveResult
            {
                Succeeded = false,
                Error = write.Error ?? "The task-sync document could not be written."
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
