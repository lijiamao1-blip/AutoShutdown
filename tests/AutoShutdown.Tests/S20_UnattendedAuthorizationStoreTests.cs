using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Unattended;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class S20_UnattendedAuthorizationStoreTests
{
    private const string FileName = "unattended.json";

    [Fact]
    public async Task Load_WhenNotFound_ReturnsNotFound()
    {
        var storage = new InMemoryStorage();
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.NotFound, result.Status);
        Assert.Null(result.Policy);
    }

    [Fact]
    public async Task Load_WhenCorrupt_ReturnsCorrupt()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.Corrupt, result.Status);
        Assert.Null(result.Policy);
    }

    [Fact]
    public async Task Load_WhenIoFailure_ReturnsIoFailure()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.IoFailure };
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.IoFailure, result.Status);
    }

    [Fact]
    public async Task Load_WhenRootIsNotObject_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, "[]");
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_WhenSchemaVersionMissing_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, "{}");
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_WhenUnsupportedVersion_ReturnsUnsupportedVersion()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 999 }""");
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.UnsupportedVersion, result.Status);
    }

    [Fact]
    public async Task Load_WhenEnabledWithoutVersionOrAction_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
            """{ "SchemaVersion": 1, "Enabled": true, "AuthorizedAtUtc": "2024-01-15T10:00:00Z" }""");
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_WhenValidDisabled_ReturnsSuccess()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 1, "Enabled": false, "AuthorizationVersion": 1 }""");
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.Success, result.Status);
        Assert.NotNull(result.Policy);
        Assert.False(result.Policy!.Enabled);
    }

    [Fact]
    public async Task Load_WhenValidEnabled_ReturnsSuccess()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
            """
            {
              "SchemaVersion": 1,
              "Enabled": true,
              "AuthorizationVersion": 3,
              "AuthorizedAtUtc": "2024-01-15T10:00:00Z",
              "AuthorizedAction": 1,
              "TriggerReason": "nightly"
            }
            """);
        var store = new UnattendedAuthorizationStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(UnattendedLoadStatus.Success, result.Status);
        Assert.Equal(3, result.Policy!.AuthorizationVersion);
        Assert.Equal(PowerAction.Shutdown, result.Policy!.AuthorizedAction);
    }

    [Fact]
    public async Task Save_WhenValid_WritesAndSucceeds()
    {
        var storage = new InMemoryStorage();
        var store = new UnattendedAuthorizationStore(storage);

        var policy = new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 1,
            AuthorizedAtUtc = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            AuthorizedAction = PowerAction.Shutdown
        };

        var result = await store.SaveAsync(policy, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(FileName, storage.WritePaths[0]);
    }

    [Fact]
    public async Task Save_WhenWriteFails_ReturnsIoFailure()
    {
        var storage = new InMemoryStorage { WriteStatus = StorageWriteStatus.IoFailure };
        var store = new UnattendedAuthorizationStore(storage);

        var policy = new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 1,
            AuthorizedAtUtc = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            AuthorizedAction = PowerAction.Shutdown
        };

        var result = await store.SaveAsync(policy, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UnattendedSaveStatus.IoFailure, result.Status);
    }

    [Fact]
    public async Task Save_WhenEnabledMissingAction_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        var store = new UnattendedAuthorizationStore(storage);

        var policy = new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 1,
            AuthorizedAtUtc = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };

        var result = await store.SaveAsync(policy, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UnattendedSaveStatus.Invalid, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

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
}
