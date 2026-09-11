using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.Core.Configuration;

public sealed class ConfigurationService : IConfigurationService
{
    private const string ConfigFileName = "config.json";
    private const int CurrentSchemaVersion = 1;

    private readonly IStorage _storage;
    private readonly IReadOnlyList<IConfigurationMigration> _migrations;
    private readonly TasksDocumentStore _tasksStore;

    public ConfigurationService(
        IStorage storage,
        IEnumerable<IConfigurationMigration>? migrations = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _migrations = migrations?.ToList() ?? [];
        _tasksStore = new TasksDocumentStore(storage);
    }

    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await _storage.ReadAsync<JsonElement>(ConfigFileName, cancellationToken)
            .ConfigureAwait(false);

        switch (read.Status)
        {
            case StorageReadStatus.NotFound:
                return Failure(ConfigurationLoadStatus.Missing, "The configuration file does not exist.");
            case StorageReadStatus.Corrupt:
                return Failure(ConfigurationLoadStatus.Corrupt, read.Error);
            case StorageReadStatus.IoFailure:
                return Failure(ConfigurationLoadStatus.IoFailure, read.Error);
            case StorageReadStatus.Success:
                break;
            default:
                return Failure(ConfigurationLoadStatus.IoFailure, "The storage layer returned an unknown status.");
        }

        var root = read.Value!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(
                ConfigurationLoadStatus.Invalid,
                "The configuration root must be a JSON object.");
        }

        if (!TryReadSchemaVersion(root, out var version))
        {
            return Failure(
                ConfigurationLoadStatus.Invalid,
                "SchemaVersion must be an integer.");
        }

        if (version > CurrentSchemaVersion)
        {
            return Failure(
                ConfigurationLoadStatus.UnsupportedVersion,
                $"SchemaVersion {version} is not supported by this version of the application.");
        }

        JsonElement element = root;
        while (version < CurrentSchemaVersion)
        {
            var migration = _migrations.FirstOrDefault(
                candidate => candidate.SourceVersion == version);

            if (migration is null || migration.TargetVersion <= migration.SourceVersion)
            {
                return Failure(
                    ConfigurationLoadStatus.MigrationUnavailable,
                    $"No migration chain exists from schema version {version}.");
            }

            element = await migration.MigrateAsync(element, cancellationToken).ConfigureAwait(false);
            version = migration.TargetVersion;
        }

        if (version != CurrentSchemaVersion)
        {
            return Failure(
                ConfigurationLoadStatus.MigrationUnavailable,
                $"The migration chain did not reach schema version {CurrentSchemaVersion}.");
        }

        AppConfig? config;
        try
        {
            config = element.Deserialize<AppConfig>();
        }
        catch (JsonException exception)
        {
            return Failure(
                ConfigurationLoadStatus.Invalid,
                $"The configuration document could not be deserialized: {exception.Message}");
        }

        if (config is null)
        {
            return Failure(
                ConfigurationLoadStatus.Invalid,
                "The configuration document contains no value.");
        }

        var errors = ConfigurationValidator.Validate(config);
        if (errors.Count > 0)
        {
            return Failure(ConfigurationLoadStatus.Invalid, errors);
        }

        return new ConfigurationLoadResult
        {
            Status = ConfigurationLoadStatus.Success,
            Config = config
        };
    }

    public async Task<ConfigurationSaveResult> SaveAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = ConfigurationValidator.Validate(config);
        if (errors.Count > 0)
        {
            return new ConfigurationSaveResult
            {
                Status = ConfigurationSaveStatus.Invalid,
                Errors = errors
            };
        }

        var write = await _storage.WriteAsync(
            ConfigFileName,
            config,
            cancellationToken).ConfigureAwait(false);

        return write.Status == StorageWriteStatus.Success
            ? new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success }
            : new ConfigurationSaveResult
            {
                Status = ConfigurationSaveStatus.IoFailure,
                Errors = [write.Error ?? "The configuration file could not be written."]
            };
    }

    public async Task<ConfigurationSaveResult> CreateSafeDefaultAsync(
        CancellationToken cancellationToken)
    {
        // 安全测试模式默认配置：明确、可验证、不启用真实电源/自启动。
        // 通过现有 SaveAsync（Validator 校验 + Storage 原子写入与备份）落盘；
        // 不在内存中冒充配置加载成功。
        var safe = new AppConfig
        {
            SchemaVersion = CurrentSchemaVersion,
            TestMode = true,
            StartWithWindows = false,
            AllowedActions =
            [
                PowerAction.Shutdown,
                PowerAction.Restart,
                PowerAction.Sleep,
                PowerAction.Hibernate
            ],
            Logging = new LoggingConfig
            {
                Level = LogLevel.Information,
                RetentionDays = 14
            }
        };

        return await SaveAsync(safe, cancellationToken).ConfigureAwait(false);
    }

    public Task<TasksLoadResult> LoadTasksAsync(CancellationToken cancellationToken)
        => _tasksStore.LoadAsync(cancellationToken);

    public Task<TasksSaveResult> SaveTasksAsync(
        TasksDocument document,
        CancellationToken cancellationToken)
        => _tasksStore.SaveAsync(document, cancellationToken);

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

    private static ConfigurationLoadResult Failure(
        ConfigurationLoadStatus status,
        string? error)
    {
        IReadOnlyList<string> errors = error is null
            ? Array.Empty<string>()
            : new[] { error };

        return new ConfigurationLoadResult
        {
            Status = status,
            Errors = errors
        };
    }

    private static ConfigurationLoadResult Failure(
        ConfigurationLoadStatus status,
        IReadOnlyList<string> errors) => new()
        {
            Status = status,
            Errors = errors
        };
}
