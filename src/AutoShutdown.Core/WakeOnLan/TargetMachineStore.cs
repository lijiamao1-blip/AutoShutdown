using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// 目标机器文档（target-machines.json）封装（S21）。复用 <see cref="IStorage"/>（FileStorage）
/// 的原子写入（临时文件 + 替换 + 备份）与损坏检测语义，不改动 FileStorage 本体。
/// 载入区分 NotFound（文件不存在）与 Corrupt（文件存在但 JSON 损坏）与 Invalid（结构非法）
/// 与 UnsupportedVersion（schema 版本不支持）；损坏/非法/不支持一律不静默回退为可用清单。
/// </summary>
public sealed class TargetMachineStore
{
    public const string FileName = "target-machines.json";

    private readonly IStorage _storage;

    public TargetMachineStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<TargetMachinesLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken).ConfigureAwait(false);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return Failure(TargetMachinesLoadStatus.NotFound, "The target machines file does not exist.");
            case StorageReadStatus.Corrupt:
                return Failure(TargetMachinesLoadStatus.Corrupt, read.Error);
            case StorageReadStatus.IoFailure:
                return Failure(TargetMachinesLoadStatus.IoFailure, read.Error);
            case StorageReadStatus.Success:
                break;
            default:
                return Failure(TargetMachinesLoadStatus.IoFailure, "The storage layer returned an unknown status.");
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(TargetMachinesLoadStatus.Invalid, "The target machines document root must be a JSON object.");
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return Failure(TargetMachinesLoadStatus.Invalid, "SchemaVersion must be an integer.");
        }

        if (version != TargetMachinesDocument.CurrentSchemaVersion)
        {
            return Failure(
                version > TargetMachinesDocument.CurrentSchemaVersion
                    ? TargetMachinesLoadStatus.UnsupportedVersion
                    : TargetMachinesLoadStatus.Invalid,
                $"SchemaVersion {version} is not supported; expected {TargetMachinesDocument.CurrentSchemaVersion}.");
        }

        TargetMachinesDocument? document;
        try
        {
            document = root.Deserialize<TargetMachinesDocument>();
        }
        catch (JsonException exception)
        {
            return Failure(TargetMachinesLoadStatus.Invalid, $"The target machines document could not be deserialized: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return Failure(TargetMachinesLoadStatus.Invalid, $"The target machines document uses an unsupported shape: {exception.Message}");
        }

        if (document is null)
        {
            return Failure(TargetMachinesLoadStatus.Invalid, "The target machines document contains no value.");
        }

        if (document.Machines is null)
        {
            return Failure(TargetMachinesLoadStatus.Invalid, "Machines must not be null.");
        }

        var machineErrors = ValidateMachines(document.Machines);
        if (machineErrors.Count > 0)
        {
            return Failure(TargetMachinesLoadStatus.Invalid, machineErrors);
        }

        return new TargetMachinesLoadResult
        {
            Status = TargetMachinesLoadStatus.Success,
            Document = document
        };
    }

    public async Task<TargetMachinesSaveResult> SaveAsync(
        TargetMachinesDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = ValidateForSave(document);
        if (errors.Count > 0)
        {
            return new TargetMachinesSaveResult
            {
                Status = TargetMachinesSaveStatus.Invalid,
                Errors = errors
            };
        }

        var write = await _storage.WriteAsync(FileName, document, cancellationToken).ConfigureAwait(false);

        return write.Status == StorageWriteStatus.Success
            ? new TargetMachinesSaveResult { Status = TargetMachinesSaveStatus.Success, BackupPath = write.BackupPath }
            : new TargetMachinesSaveResult
            {
                Status = TargetMachinesSaveStatus.IoFailure,
                Errors = [write.Error ?? "The target machines file could not be written."]
            };
    }

    private static IReadOnlyList<string> ValidateForSave(TargetMachinesDocument document)
    {
        var errors = new List<string>();

        if (document.SchemaVersion != TargetMachinesDocument.CurrentSchemaVersion)
        {
            errors.Add(
                $"SchemaVersion must be {TargetMachinesDocument.CurrentSchemaVersion}, but was {document.SchemaVersion}.");
        }

        if (document.Machines is null)
        {
            errors.Add("Machines must not be null.");
        }
        else
        {
            errors.AddRange(ValidateMachines(document.Machines));
        }

        return errors;
    }

    private static IReadOnlyList<string> ValidateMachines(IReadOnlyList<WakeOnLanTarget> machines)
    {
        var errors = new List<string>();
        var seenIds = new HashSet<Guid>();

        for (var index = 0; index < machines.Count; index++)
        {
            var machine = machines[index];
            if (machine is null)
            {
                errors.Add($"Machines[{index}] must not be null.");
                continue;
            }

            var structuralError = WakeOnLanTarget.GetStructuralError(machine);
            if (structuralError is not null)
            {
                errors.Add($"Machine {machine.Id} is structurally invalid: {structuralError}");
                continue;
            }

            if (!seenIds.Add(machine.Id))
            {
                errors.Add($"Machine id {machine.Id} is duplicated.");
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

    private static TargetMachinesLoadResult Failure(TargetMachinesLoadStatus status, string? error)
    {
        IReadOnlyList<string> errors = error is null
            ? Array.Empty<string>()
            : new[] { error };

        return new TargetMachinesLoadResult
        {
            Status = status,
            Errors = errors
        };
    }

    private static TargetMachinesLoadResult Failure(
        TargetMachinesLoadStatus status,
        IReadOnlyList<string> errors) => new()
        {
            Status = status,
            Errors = errors
        };
}

public enum TargetMachinesSaveStatus
{
    Unknown = 0,
    Success = 1,
    Invalid = 2,
    IoFailure = 3
}

public sealed record TargetMachinesSaveResult
{
    public TargetMachinesSaveStatus Status { get; init; } = TargetMachinesSaveStatus.Unknown;

    public IReadOnlyList<string> Errors { get; init; } = [];

    public string? BackupPath { get; init; }

    public bool Succeeded => Status == TargetMachinesSaveStatus.Success;
}
