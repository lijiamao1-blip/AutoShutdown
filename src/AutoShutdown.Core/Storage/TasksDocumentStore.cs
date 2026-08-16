using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Storage;

/// <summary>
/// tasks.json 文档封装：通过 <see cref="IStorage"/>（FileStorage）复用原子写入与损坏检测语义，
/// 不改动 FileStorage 本体。明确区分 NotFound（文件不存在）与 Corrupt（文件存在但 JSON 损坏），
/// 损坏数据绝不回退为空任务清单或任何可能造成危险行为的默认任务。
/// </summary>
public sealed class TasksDocumentStore
{
    public const string FileName = "tasks.json";

    private readonly IStorage _storage;

    public TasksDocumentStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<TasksLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return Failure(TasksLoadStatus.NotFound, "The tasks file does not exist.");
            case StorageReadStatus.Corrupt:
                return Failure(TasksLoadStatus.Corrupt, read.Error);
            case StorageReadStatus.IoFailure:
                return Failure(TasksLoadStatus.IoFailure, read.Error);
            case StorageReadStatus.Success:
                break;
            default:
                return Failure(TasksLoadStatus.IoFailure, "The storage layer returned an unknown status.");
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(TasksLoadStatus.Invalid, "The tasks document root must be a JSON object.");
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return Failure(TasksLoadStatus.Invalid, "SchemaVersion must be an integer.");
        }

        if (version == 1)
        {
            // V1→V2 身份迁移（S14）：仅新增可空字段，逐任务数据无损保留。
            return await MigrateV1Async(root, cancellationToken).ConfigureAwait(false);
        }

        if (version != TasksDocument.CurrentSchemaVersion)
        {
            return Failure(
                version > TasksDocument.CurrentSchemaVersion
                    ? TasksLoadStatus.UnsupportedVersion
                    : TasksLoadStatus.Invalid,
                $"SchemaVersion {version} is not supported; expected {TasksDocument.CurrentSchemaVersion}.");
        }

        TasksDocument? document;
        try
        {
            document = root.Deserialize<TasksDocument>();
        }
        catch (JsonException exception)
        {
            return Failure(
                TasksLoadStatus.Invalid,
                $"The tasks document could not be deserialized: {exception.Message}");
        }

        if (document is null)
        {
            return Failure(TasksLoadStatus.Invalid, "The tasks document contains no value.");
        }

        if (document.Tasks is null)
        {
            return Failure(TasksLoadStatus.Invalid, "Tasks must not be null.");
        }

        var taskErrors = ValidateTasks(document.Tasks);
        if (taskErrors.Count > 0)
        {
            return Failure(TasksLoadStatus.Invalid, taskErrors);
        }

        return new TasksLoadResult
        {
            Status = TasksLoadStatus.Success,
            Document = document
        };
    }

    public async Task<TasksSaveResult> SaveAsync(
        TasksDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = ValidateForSave(document);
        if (errors.Count > 0)
        {
            return new TasksSaveResult
            {
                Status = TasksSaveStatus.Invalid,
                Errors = errors
            };
        }

        var write = await _storage.WriteAsync(FileName, document, cancellationToken);

        return write.Status == StorageWriteStatus.Success
            ? new TasksSaveResult { Status = TasksSaveStatus.Success }
            : new TasksSaveResult
            {
                Status = TasksSaveStatus.IoFailure,
                Errors = [write.Error ?? "The tasks file could not be written."]
            };
    }

    /// <summary>
    /// V1→V2 身份迁移（S14）：V1 只缺少 S14 新增的可空字段（Weekdays/NthWorkday/
    /// OneTimeDateTime/HolidayDates），逐任务数据无损保留；仅提升 SchemaVersion 后
    /// 原子写回（FileStorage 自动保留旧文件 .bak 备份）。迁移失败不覆盖原文件。
    /// </summary>
    private async Task<TasksLoadResult> MigrateV1Async(
        JsonElement v1Root,
        CancellationToken cancellationToken)
    {
        TasksDocument? document;
        try
        {
            document = v1Root.Deserialize<TasksDocument>();
        }
        catch (JsonException exception)
        {
            return Failure(
                TasksLoadStatus.Invalid,
                $"The V1 tasks document could not be deserialized: {exception.Message}");
        }

        if (document is null)
        {
            return Failure(TasksLoadStatus.Invalid, "The V1 tasks document contains no value.");
        }

        if (document.Tasks is null)
        {
            return Failure(TasksLoadStatus.Invalid, "Tasks must not be null.");
        }

        var taskErrors = ValidateTasks(document.Tasks);
        if (taskErrors.Count > 0)
        {
            return Failure(TasksLoadStatus.Invalid, taskErrors);
        }

        var migrated = new TasksDocument
        {
            SchemaVersion = TasksDocument.CurrentSchemaVersion,
            Tasks = document.Tasks
        };

        var write = await _storage.WriteAsync(FileName, migrated, cancellationToken).ConfigureAwait(false);
        if (!write.Succeeded)
        {
            return Failure(
                TasksLoadStatus.IoFailure,
                "The V1 tasks document could not be migrated: " + (write.Error ?? "unknown error."));
        }

        return new TasksLoadResult
        {
            Status = TasksLoadStatus.Migrated,
            Document = migrated
        };
    }

    private static IReadOnlyList<string> ValidateForSave(TasksDocument document)
    {
        var errors = new List<string>();

        if (document.SchemaVersion != TasksDocument.CurrentSchemaVersion)
        {
            errors.Add(
                $"SchemaVersion must be {TasksDocument.CurrentSchemaVersion}, but was {document.SchemaVersion}.");
        }

        if (document.Tasks is null)
        {
            errors.Add("Tasks must not be null.");
        }
        else
        {
            errors.AddRange(ValidateTasks(document.Tasks));
        }

        return errors;
    }

    private static IReadOnlyList<string> ValidateTasks(IReadOnlyList<TaskDefinition> tasks)
    {
        var errors = new List<string>();
        var seenIds = new HashSet<Guid>();

        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            if (task is null)
            {
                errors.Add($"Tasks[{index}] must not be null.");
                continue;
            }

            var structuralError = TaskDefinitionValidator.GetStructuralError(task);
            if (structuralError is not null)
            {
                errors.Add($"Task {task.Id} is structurally invalid: {structuralError}");
                continue;
            }

            if (!seenIds.Add(task.Id))
            {
                errors.Add($"Task id {task.Id} is duplicated.");
            }
        }

        return errors;
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

    private static TasksLoadResult Failure(TasksLoadStatus status, string? error)
    {
        IReadOnlyList<string> errors = error is null
            ? Array.Empty<string>()
            : new[] { error };

        return new TasksLoadResult
        {
            Status = status,
            Errors = errors
        };
    }

    private static TasksLoadResult Failure(
        TasksLoadStatus status,
        IReadOnlyList<string> errors) => new()
        {
            Status = status,
            Errors = errors
        };
}

public enum TasksLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6,

    /// <summary>V1 文档已无损迁移到 V2（S14）。</summary>
    Migrated = 7
}

public sealed record TasksLoadResult
{
    public TasksLoadStatus Status { get; init; } = TasksLoadStatus.Unknown;

    public TasksDocument? Document { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public enum TasksSaveStatus
{
    Unknown = 0,
    Success = 1,
    Invalid = 2,
    IoFailure = 3
}

public sealed record TasksSaveResult
{
    public TasksSaveStatus Status { get; init; } = TasksSaveStatus.Unknown;

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Succeeded => Status == TasksSaveStatus.Success;
}
