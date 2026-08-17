using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Unattended;

/// <summary>
/// 无人值守授权记录（unattended.json）文档封装。复用 <see cref="IStorage"/>（FileStorage）
/// 的原子写入（临时文件 + 替换 + 备份）与损坏检测语义，不改动 FileStorage 本体。
/// 载入区分 NotFound（文件不存在）与 Corrupt（文件存在但 JSON 损坏）与 Invalid（结构非法）
/// 与 UnsupportedVersion（schema 版本不支持）；损坏/非法/不支持一律不静默回退为可用策略。
/// </summary>
public sealed class UnattendedAuthorizationStore
{
    public const string FileName = "unattended.json";

    private readonly IStorage _storage;

    public UnattendedAuthorizationStore(IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<UnattendedLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(FileName, cancellationToken).ConfigureAwait(false);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return Failure(UnattendedLoadStatus.NotFound, "The unattended authorization record does not exist.");
            case StorageReadStatus.Corrupt:
                return Failure(UnattendedLoadStatus.Corrupt, read.Error);
            case StorageReadStatus.IoFailure:
                return Failure(UnattendedLoadStatus.IoFailure, read.Error);
            case StorageReadStatus.Success:
                break;
            default:
                return Failure(UnattendedLoadStatus.IoFailure, "The storage layer returned an unknown status.");
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(UnattendedLoadStatus.Invalid, "The unattended authorization root must be a JSON object.");
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return Failure(UnattendedLoadStatus.Invalid, "SchemaVersion must be an integer.");
        }

        if (version != UnattendedPolicy.CurrentSchemaVersion)
        {
            return Failure(
                version > UnattendedPolicy.CurrentSchemaVersion
                    ? UnattendedLoadStatus.UnsupportedVersion
                    : UnattendedLoadStatus.Invalid,
                $"SchemaVersion {version} is not supported; expected {UnattendedPolicy.CurrentSchemaVersion}.");
        }

        UnattendedPolicy? policy;
        try
        {
            policy = root.Deserialize<UnattendedPolicy>();
        }
        catch (JsonException exception)
        {
            return Failure(UnattendedLoadStatus.Invalid, $"The unattended authorization could not be deserialized: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return Failure(UnattendedLoadStatus.Invalid, $"The unattended authorization uses an unsupported shape: {exception.Message}");
        }

        if (policy is null)
        {
            return Failure(UnattendedLoadStatus.Invalid, "The unattended authorization contains no value.");
        }

        var errors = Validate(policy);
        if (errors.Count > 0)
        {
            return Failure(UnattendedLoadStatus.Invalid, errors);
        }

        return new UnattendedLoadResult
        {
            Status = UnattendedLoadStatus.Success,
            Policy = policy
        };
    }

    public async Task<UnattendedSaveResult> SaveAsync(
        UnattendedPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var errors = Validate(policy);
        if (errors.Count > 0)
        {
            return new UnattendedSaveResult
            {
                Status = UnattendedSaveStatus.Invalid,
                Errors = errors
            };
        }

        var write = await _storage.WriteAsync(FileName, policy, cancellationToken).ConfigureAwait(false);

        return write.Status == StorageWriteStatus.Success
            ? new UnattendedSaveResult { Status = UnattendedSaveStatus.Success }
            : new UnattendedSaveResult
            {
                Status = UnattendedSaveStatus.IoFailure,
                Errors = [write.Error ?? "The unattended authorization could not be written."]
            };
    }

    private static IReadOnlyList<string> Validate(UnattendedPolicy policy)
    {
        var errors = new List<string>();

        if (policy.SchemaVersion != UnattendedPolicy.CurrentSchemaVersion)
        {
            errors.Add(
                $"SchemaVersion must be {UnattendedPolicy.CurrentSchemaVersion}, but was {policy.SchemaVersion}.");
        }

        if (policy.Enabled)
        {
            if (policy.AuthorizationVersion <= 0)
            {
                errors.Add("AuthorizationVersion must be positive when enabled.");
            }

            if (policy.AuthorizedAtUtc == default)
            {
                errors.Add("AuthorizedAtUtc must be set when enabled.");
            }

            if (policy.AuthorizedAction == PowerAction.Unknown)
            {
                errors.Add("AuthorizedAction must be a valid power action when enabled.");
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

    private static UnattendedLoadResult Failure(UnattendedLoadStatus status, string? error)
    {
        IReadOnlyList<string> errors = error is null
            ? Array.Empty<string>()
            : new[] { error };

        return new UnattendedLoadResult
        {
            Status = status,
            Errors = errors
        };
    }

    private static UnattendedLoadResult Failure(
        UnattendedLoadStatus status,
        IReadOnlyList<string> errors) => new()
        {
            Status = status,
            Errors = errors
        };
}

public enum UnattendedSaveStatus
{
    Unknown = 0,
    Success = 1,
    Invalid = 2,
    IoFailure = 3
}

public sealed record UnattendedSaveResult
{
    public UnattendedSaveStatus Status { get; init; } = UnattendedSaveStatus.Unknown;

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Succeeded => Status == UnattendedSaveStatus.Success;
}
