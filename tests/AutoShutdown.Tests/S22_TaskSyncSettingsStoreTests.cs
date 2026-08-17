using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 CP4 测试：task-sync.json 严格分类读取（NotFound/Corrupt/Invalid/UnsupportedVersion/
/// IoFailure）与原子写入；损坏或非法绝不静默回退为「启用同步」（默认安全值关闭）。
/// </summary>
public sealed class S22_TaskSyncSettingsStoreTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 8, 17, 2, 3, 4, TimeSpan.Zero);

    // ---- 读取分类 ----

    [Fact]
    public async Task Load_FileMissing_ReturnsNotFound()
    {
        var store = new TaskSyncSettingsStore(new InMemoryStorage());
        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.NotFound, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task Load_RootNotObject_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(TaskSyncSettingsStore.FileName, "[1,2,3]");

        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Invalid, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task Load_SchemaVersionMissing_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(TaskSyncSettingsStore.FileName, "{}");

        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_SchemaVersionNotInteger_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(TaskSyncSettingsStore.FileName, """{"SchemaVersion":"abc"}""");

        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_OlderSchemaVersion_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(TaskSyncSettingsStore.FileName, """{"SchemaVersion":0}""");

        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_NewerSchemaVersion_ReturnsUnsupportedVersion()
    {
        var storage = new InMemoryStorage();
        storage.Seed(TaskSyncSettingsStore.FileName, """{"SchemaVersion":99}""");

        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.UnsupportedVersion, result.Status);
    }

    [Fact]
    public async Task Load_FieldOfWrongType_ReturnsInvalid()
    {
        // Enabled 字段类型错误：系统文本反序列化会抛 JsonException，绝不静默按默认值解析。
        var storage = new InMemoryStorage();
        storage.Seed(TaskSyncSettingsStore.FileName, """{"SchemaVersion":1,"Enabled":"not-a-bool"}""");

        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_CorruptJson_ReturnsCorrupt()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task Load_IoFailure_ReturnsIoFailure()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.IoFailure };
        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.IoFailure, result.Status);
    }

    [Fact]
    public async Task Load_ValidDocument_ReturnsSuccessWithDefaultClosed()
    {
        var storage = new InMemoryStorage();
        storage.Seed(TaskSyncSettingsStore.FileName, """{"SchemaVersion":1}""");

        var result = await store(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Success, result.Status);
        Assert.NotNull(result.Document);
        Assert.False(result.Document!.Enabled); // 未显式开启 → 同步关闭（绝不默认触发）。
        Assert.Null(result.Document.LastSyncAtUtc);
    }

    // ---- 写入 ----

    [Fact]
    public async Task Save_WrongSchemaVersion_FailsWithoutWrite()
    {
        var storage = new InMemoryStorage();
        var result = await store(storage).SaveAsync(
            new TaskSyncSettingsDocument { SchemaVersion = 99 },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task Save_StorageFailure_ReportsFailure()
    {
        var storage = new InMemoryStorage { WriteStatus = StorageWriteStatus.IoFailure };
        var result = await store(storage).SaveAsync(
            new TaskSyncSettingsDocument { Enabled = true },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task Save_ValidDocument_WritesAtomicallyThroughStorage()
    {
        var storage = new InMemoryStorage();
        var storeInstance = store(storage);
        var result = await storeInstance.SaveAsync(
            new TaskSyncSettingsDocument { Enabled = true, LastSyncAtUtc = SampleTime },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(TaskSyncSettingsStore.FileName, Assert.Single(storage.WritePaths));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEnabledAndTimestamp()
    {
        var storage = new WritingStorage();
        var storeInstance = store(storage);
        var save = await storeInstance.SaveAsync(
            new TaskSyncSettingsDocument { Enabled = true, LastSyncAtUtc = SampleTime },
            CancellationToken.None);
        Assert.True(save.Succeeded);

        var load = await storeInstance.LoadAsync(CancellationToken.None);

        Assert.Equal(TaskSyncSettingsLoadStatus.Success, load.Status);
        Assert.NotNull(load.Document);
        Assert.True(load.Document!.Enabled);
        Assert.Equal(SampleTime, load.Document.LastSyncAtUtc);
    }

    private static TaskSyncSettingsStore store(IStorage storage) => new(storage);

    /// <summary>回环测试用：写入时持久化文档，允许 Save→Load 闭环验证序列化对称性。</summary>
    private sealed class WritingStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _documents = new();

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
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
            using var stream = new MemoryStream();
            JsonSerializer.Serialize(stream, value);
            stream.Position = 0;
            using var document = JsonDocument.Parse(stream);
            _documents[relativePath] = document.RootElement.Clone();

            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success,
                BackupPath = relativePath + ".bak"
            });
        }
    }
}
