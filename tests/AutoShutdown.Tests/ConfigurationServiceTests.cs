using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class ConfigurationServiceTests
{
    private const string ValidJson =
        """{"SchemaVersion":1,"DefaultWarningSeconds":60,"DefaultSnoozeSeconds":300,"AllowedActions":[1,2],"Logging":{"Level":1,"RetentionDays":14}}""";

    private const string MigrationSourceJson =
        """{"SchemaVersion":0,"DefaultWarningSeconds":60,"DefaultSnoozeSeconds":300,"AllowedActions":[1],"Logging":{"Level":1,"RetentionDays":14}}""";

    [Fact]
    public async Task LoadAsync_WhenConfigIsValid_ReturnsSuccessWithConfig()
    {
        var storage = new InMemoryStorage();
        storage.Seed("config.json", ValidJson);
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Success, result.Status);
        var config = result.Config;
        Assert.NotNull(config);
        Assert.Equal(1, config!.SchemaVersion);
        Assert.Equal(2, config.AllowedActions.Length);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsMissing_ReturnsMissingWithNullConfig()
    {
        var service = new ConfigurationService(new InMemoryStorage());

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Missing, result.Status);
        Assert.Null(result.Config);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task LoadAsync_WhenStorageReportsCorrupt_ReturnsCorruptWithNullConfig()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Corrupt, result.Status);
        Assert.Null(result.Config);
    }

    [Fact]
    public async Task LoadAsync_WhenStorageReportsIoFailure_ReturnsIoFailure()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.IoFailure };
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.IoFailure, result.Status);
        Assert.Null(result.Config);
    }

    [Fact]
    public async Task LoadAsync_WhenRootIsNotAnObject_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed("config.json", "[1,2]");
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Invalid, result.Status);
        Assert.Null(result.Config);
    }

    [Theory]
    [InlineData("""{"DefaultWarningSeconds":60}""")]
    [InlineData("""{"SchemaVersion":"1"}""")]
    [InlineData("""{"SchemaVersion":1.5}""")]
    public async Task LoadAsync_WhenSchemaVersionIsMissingOrNotAnInteger_ReturnsInvalid(string json)
    {
        var storage = new InMemoryStorage();
        storage.Seed("config.json", json);
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Invalid, result.Status);
        Assert.Null(result.Config);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionIsNewerThanCurrent_ReturnsUnsupportedVersion()
    {
        var storage = new InMemoryStorage();
        storage.Seed("config.json", """{"SchemaVersion":2}""");
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Config);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionIsOlder_AndNoMigrationsRegistered_ReturnsMigrationUnavailable()
    {
        var storage = new InMemoryStorage();
        storage.Seed("config.json", """{"SchemaVersion":0}""");
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.MigrationUnavailable, result.Status);
        Assert.Null(result.Config);
    }

    [Fact]
    public async Task LoadAsync_WhenCompleteMigrationChainExists_ReturnsSuccess()
    {
        var storage = new InMemoryStorage();
        storage.Seed("config.json", MigrationSourceJson);
        var migration = new FakeMigration(0, 1);
        var service = new ConfigurationService(storage, [migration]);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Success, result.Status);
        var config = result.Config;
        Assert.NotNull(config);
        Assert.Equal(1, config!.SchemaVersion);
        Assert.Equal(1, migration.CallCount);
    }

    [Theory]
    [InlineData("""{"SchemaVersion":1,"DefaultWarningSeconds":-1}""")]
    [InlineData("""{"SchemaVersion":1,"DefaultWarningSeconds":86401}""")]
    [InlineData("""{"SchemaVersion":1,"DefaultSnoozeSeconds":0}""")]
    [InlineData("""{"SchemaVersion":1,"DefaultSnoozeSeconds":86401}""")]
    [InlineData("""{"SchemaVersion":1,"AllowedActions":null}""")]
    [InlineData("""{"SchemaVersion":1,"AllowedActions":[]}""")]
    [InlineData("""{"SchemaVersion":1,"AllowedActions":[1,1]}""")]
    [InlineData("""{"SchemaVersion":1,"AllowedActions":[0]}""")]
    [InlineData("""{"SchemaVersion":1,"Logging":null}""")]
    [InlineData("""{"SchemaVersion":1,"Logging":{"Level":0,"RetentionDays":14}}""")]
    [InlineData("""{"SchemaVersion":1,"Logging":{"Level":1,"RetentionDays":0}}""")]
    [InlineData("""{"SchemaVersion":1,"Logging":{"Level":1,"RetentionDays":366}}""")]
    public async Task LoadAsync_WhenAFieldRuleIsViolated_ReturnsInvalid(string json)
    {
        var storage = new InMemoryStorage();
        storage.Seed("config.json", json);
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Invalid, result.Status);
        Assert.Null(result.Config);
    }

    [Fact]
    public async Task LoadAsync_WhenMultipleRulesAreViolated_ReturnsAllErrors()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            "config.json",
            """{"SchemaVersion":1,"DefaultSnoozeSeconds":0,"AllowedActions":[],"Logging":{"Level":0,"RetentionDays":14}}""");
        var service = new ConfigurationService(storage);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadStatus.Invalid, result.Status);
        Assert.Null(result.Config);
        Assert.True(result.Errors.Count >= 3);
    }

    [Fact]
    public async Task SaveAsync_WhenConfigIsInvalid_DoesNotWrite()
    {
        var storage = new InMemoryStorage();
        var service = new ConfigurationService(storage);

        var result = await service.SaveAsync(
            ValidConfig() with { DefaultSnoozeSeconds = 0 },
            CancellationToken.None);

        Assert.Equal(ConfigurationSaveStatus.Invalid, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task SaveAsync_WhenConfigIsValid_WritesOnceToConfigJson()
    {
        var storage = new InMemoryStorage();
        var service = new ConfigurationService(storage);

        var result = await service.SaveAsync(ValidConfig(), CancellationToken.None);

        Assert.Equal(ConfigurationSaveStatus.Success, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(new[] { "config.json" }, storage.WritePaths);
    }

    [Fact]
    public async Task SaveAsync_WhenStorageWriteFails_ReturnsFailure()
    {
        var storage = new InMemoryStorage { WriteStatus = StorageWriteStatus.IoFailure };
        var service = new ConfigurationService(storage);

        var result = await service.SaveAsync(ValidConfig(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotEqual(ConfigurationSaveStatus.Success, result.Status);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validator_WhenConfigIsValid_ReportsNoErrors()
    {
        Assert.Empty(ConfigurationValidator.Validate(ValidConfig()));
    }

    [Fact]
    public void Validator_WhenSchemaVersionIsNotOne_ReportsError()
    {
        var errors = ConfigurationValidator.Validate(ValidConfig() with { SchemaVersion = 2 });

        Assert.Contains(errors, error => error.Contains("SchemaVersion"));
    }

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        DefaultWarningSeconds = 60,
        DefaultSnoozeSeconds = 300,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _documents = new();
        private readonly List<string> _writePaths = new();

        public StorageReadStatus ReadStatus { get; init; } = StorageReadStatus.Success;

        public StorageWriteStatus WriteStatus { get; init; } = StorageWriteStatus.Success;

        public int WriteCount => _writePaths.Count;

        public IReadOnlyList<string> WritePaths => _writePaths;

        public void Seed(string relativePath, string json)
        {
            using var document = JsonDocument.Parse(json);
            _documents[relativePath] = document.RootElement.Clone();
        }

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (ReadStatus != StorageReadStatus.Success)
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = ReadStatus,
                    Error = "Simulated read failure."
                });
            }

            if (!_documents.TryGetValue(relativePath, out var element))
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.NotFound
                });
            }

            return Task.FromResult(new StorageReadResult<T>
            {
                Status = StorageReadStatus.Success,
                Value = element.Deserialize<T>()
            });
        }

        public Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
        {
            if (WriteStatus != StorageWriteStatus.Success)
            {
                return Task.FromResult(new StorageWriteResult
                {
                    Status = WriteStatus,
                    Error = "Simulated write failure."
                });
            }

            _writePaths.Add(relativePath);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
        }
    }

    private sealed class FakeMigration : IConfigurationMigration
    {
        public FakeMigration(int sourceVersion, int targetVersion)
        {
            SourceVersion = sourceVersion;
            TargetVersion = targetVersion;
        }

        public int SourceVersion { get; }

        public int TargetVersion { get; }

        public int CallCount { get; private set; }

        public Task<JsonElement> MigrateAsync(
            JsonElement source,
            CancellationToken cancellationToken)
        {
            CallCount++;

            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (var property in source.EnumerateObject())
                {
                    if (property.Name == "SchemaVersion")
                    {
                        writer.WriteNumber("SchemaVersion", TargetVersion);
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(output.ToArray());
            return Task.FromResult(document.RootElement.Clone());
        }
    }
}
