using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Storage;

/// <summary>
/// runtime.json 文档封装：复用 <see cref="IStorage"/>（FileStorage）的原子写入与损坏检测语义，
/// 不改动 FileStorage 本体。区分 NotFound（文件不存在）与 Corrupt（文件存在但 JSON 损坏），
/// 损坏数据绝不回退为空态静默吞掉。
///
/// V2 多实例 schema（SchemaVersion=2，Instances 按任务 id 组织）为当前唯一受支持版本。
/// 旧 V1 单实例（SchemaVersion=1 + CurrentInstance）按 GATE-Q3「备份后重建」处理：
/// 先备份 runtime.json → runtime.json.v1bak，再以空的多实例态重建；不执行逐任务迁移、
/// 不伪造任务 id、不保留 V1 单实例模型。
/// </summary>
public sealed class RuntimeStateStore
{
    public const string FileName = "runtime.json";
    public const string V1BackupFileName = "runtime.json.v1bak";

    private readonly IStorage _storage;

    public RuntimeStateStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<RuntimeStateLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken).ConfigureAwait(false);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return Failure(RuntimeStateLoadStatus.NotFound, "The runtime state file does not exist.");
            case StorageReadStatus.Corrupt:
                return Failure(RuntimeStateLoadStatus.Corrupt, read.Error);
            case StorageReadStatus.IoFailure:
                return Failure(RuntimeStateLoadStatus.IoFailure, read.Error);
            case StorageReadStatus.Success:
                break;
            default:
                return Failure(RuntimeStateLoadStatus.IoFailure, "The storage layer returned an unknown status.");
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(RuntimeStateLoadStatus.Invalid, "The runtime state document root must be a JSON object.");
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return Failure(RuntimeStateLoadStatus.Invalid, "SchemaVersion must be an integer.");
        }

        if (version == 1 && root.TryGetProperty("CurrentInstance", out _))
        {
            return await MigrateV1Async(root, cancellationToken).ConfigureAwait(false);
        }

        if (version != RuntimeState.CurrentSchemaVersion)
        {
            return Failure(
                version > RuntimeState.CurrentSchemaVersion
                    ? RuntimeStateLoadStatus.UnsupportedVersion
                    : RuntimeStateLoadStatus.Invalid,
                $"SchemaVersion {version} is not supported; expected {RuntimeState.CurrentSchemaVersion}.");
        }

        RuntimeState? state;
        try
        {
            state = root.Deserialize<RuntimeState>();
        }
        catch (JsonException exception)
        {
            return Failure(
                RuntimeStateLoadStatus.Invalid,
                $"The runtime state could not be deserialized: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return Failure(
                RuntimeStateLoadStatus.Invalid,
                $"The runtime state uses an unsupported shape: {exception.Message}");
        }

        if (state is null)
        {
            return Failure(RuntimeStateLoadStatus.Invalid, "The runtime state contains no value.");
        }

        if (state.Instances is null)
        {
            return Failure(RuntimeStateLoadStatus.Invalid, "Instances must not be null.");
        }

        var instanceErrors = ValidateInstances(state.Instances);
        if (instanceErrors.Count > 0)
        {
            return Failure(RuntimeStateLoadStatus.Invalid, instanceErrors);
        }

        return new RuntimeStateLoadResult
        {
            Status = RuntimeStateLoadStatus.Success,
            State = state
        };
    }

    public async Task<RuntimeStateSaveResult> SaveAsync(
        RuntimeState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var errors = ValidateForSave(state);
        if (errors.Count > 0)
        {
            return new RuntimeStateSaveResult
            {
                Status = RuntimeStateSaveStatus.Invalid,
                Errors = errors
            };
        }

        var write = await _storage.WriteAsync(FileName, state, cancellationToken).ConfigureAwait(false);

        return write.Status == StorageWriteStatus.Success
            ? new RuntimeStateSaveResult { Status = RuntimeStateSaveStatus.Success }
            : new RuntimeStateSaveResult
            {
                Status = RuntimeStateSaveStatus.IoFailure,
                Errors = [write.Error ?? "The runtime state could not be written."]
            };
    }

    /// <summary>
    /// GATE-Q3：备份旧 V1 单实例 runtime.json 为 runtime.json.v1bak，再以空 V2 多实例态重建。
    /// 备份失败不覆盖原文件、不重建；重建失败返回 IoFailure（原文件与备份均保留，可重试）。
    /// </summary>
    private async Task<RuntimeStateLoadResult> MigrateV1Async(
        JsonElement v1Root,
        CancellationToken cancellationToken)
    {
        var backupWrite = await _storage.WriteAsync(V1BackupFileName, v1Root, cancellationToken)
            .ConfigureAwait(false);
        if (!backupWrite.Succeeded)
        {
            return Failure(
                RuntimeStateLoadStatus.IoFailure,
                "Failed to back up the V1 runtime state: " + (backupWrite.Error ?? "unknown error."));
        }

        var emptyState = new RuntimeState
        {
            SchemaVersion = RuntimeState.CurrentSchemaVersion,
            Instances = new Dictionary<Guid, TaskInstance>(),
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        var rebuildWrite = await _storage.WriteAsync(FileName, emptyState, cancellationToken)
            .ConfigureAwait(false);
        if (!rebuildWrite.Succeeded)
        {
            return Failure(
                RuntimeStateLoadStatus.IoFailure,
                "Failed to rebuild the empty V2 runtime state: " + (rebuildWrite.Error ?? "unknown error."));
        }

        return new RuntimeStateLoadResult
        {
            Status = RuntimeStateLoadStatus.Migrated,
            State = emptyState,
            BackupPath = V1BackupFileName
        };
    }

    private static IReadOnlyList<string> ValidateForSave(RuntimeState state)
    {
        var errors = new List<string>();

        if (state.SchemaVersion != RuntimeState.CurrentSchemaVersion)
        {
            errors.Add(
                $"SchemaVersion must be {RuntimeState.CurrentSchemaVersion}, but was {state.SchemaVersion}.");
        }

        if (state.Instances is null)
        {
            errors.Add("Instances must not be null.");
        }
        else
        {
            errors.AddRange(ValidateInstances(state.Instances));
        }

        return errors;
    }

    private static IReadOnlyList<string> ValidateInstances(IReadOnlyDictionary<Guid, TaskInstance> instances)
    {
        var errors = new List<string>();

        foreach (var (taskId, instance) in instances)
        {
            if (instance is null)
            {
                errors.Add($"Instances[{taskId}] must not be null.");
                continue;
            }

            if (taskId != instance.SourceTaskId)
            {
                errors.Add(
                    $"Instance key {taskId} does not match its SourceTaskId {instance.SourceTaskId}.");
            }

            if (instance.InstanceId == Guid.Empty)
            {
                errors.Add($"Instance {taskId} has an empty InstanceId.");
            }

            if (instance.SourceTaskId == Guid.Empty)
            {
                errors.Add($"Instance {taskId} has an empty SourceTaskId.");
            }

            if (instance.StageToken == Guid.Empty)
            {
                errors.Add($"Instance {taskId} has an empty StageToken.");
            }

            if (instance.State == TaskInstanceState.Unknown)
            {
                errors.Add($"Instance {taskId} has an Unknown state.");
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

    private static RuntimeStateLoadResult Failure(RuntimeStateLoadStatus status, string? error)
    {
        IReadOnlyList<string> errors = error is null
            ? Array.Empty<string>()
            : new[] { error };

        return new RuntimeStateLoadResult
        {
            Status = status,
            Errors = errors
        };
    }

    private static RuntimeStateLoadResult Failure(
        RuntimeStateLoadStatus status,
        IReadOnlyList<string> errors) => new()
        {
            Status = status,
            Errors = errors
        };
}

public enum RuntimeStateLoadStatus
{
    Unknown = 0,
    Success = 1,
    NotFound = 2,
    Corrupt = 3,
    IoFailure = 4,
    Invalid = 5,
    UnsupportedVersion = 6,

    /// <summary>旧 V1 单实例已备份为 .v1bak 并以空 V2 态重建（GATE-Q3）。</summary>
    Migrated = 7
}

public sealed record RuntimeStateLoadResult
{
    public RuntimeStateLoadStatus Status { get; init; } = RuntimeStateLoadStatus.Unknown;

    public RuntimeState? State { get; init; }

    public string? BackupPath { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public enum RuntimeStateSaveStatus
{
    Unknown = 0,
    Success = 1,
    Invalid = 2,
    IoFailure = 3
}

public sealed record RuntimeStateSaveResult
{
    public RuntimeStateSaveStatus Status { get; init; } = RuntimeStateSaveStatus.Unknown;

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Succeeded => Status == RuntimeStateSaveStatus.Success;
}
