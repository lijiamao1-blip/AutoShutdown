using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.WakeOnLan;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>S21-C1：目标机器配置读取区分 NotFound/Corrupt/Invalid/UnsupportedVersion，写入复用原子存储。</summary>
public sealed class S21_TargetMachineStoreTests
{
    private const string FileName = "target-machines.json";

    [Fact]
    public async Task Load_WhenNotFound_ReturnsNotFound()
    {
        var store = new TargetMachineStore(new InMemoryStorage());

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.NotFound, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task Load_WhenCorrupt_ReturnsCorrupt()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task Load_WhenIoFailure_ReturnsIoFailure()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.IoFailure };
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.IoFailure, result.Status);
    }

    [Fact]
    public async Task Load_WhenRootIsNotObject_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, "[]");
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_WhenSchemaVersionMissing_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, "{}");
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_WhenUnsupportedVersion_ReturnsUnsupportedVersion()
    {
        var storage = new InMemoryStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 999, "Machines": [] }""");
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.UnsupportedVersion, result.Status);
    }

    [Fact]
    public async Task Load_WhenInvalidMachine_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
            """
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "00000000-0000-0000-0000-000000000000", "Name": "X", "Mac": "not-a-mac" }
              ]
            }
            """);
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_WhenDuplicateIds_ReturnsInvalid()
    {
        var storage = new InMemoryStorage();
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        storage.Seed(
            FileName,
            $$"""
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "{{id}}", "Name": "A", "Mac": "AA:BB:CC:DD:EE:FF" },
                { "Id": "{{id}}", "Name": "B", "Mac": "AA:BB:CC:DD:EE:F0" }
              ]
            }
            """);
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Load_WhenValid_ReturnsSuccess()
    {
        var storage = new InMemoryStorage();
        storage.Seed(
            FileName,
            """
            {
              "SchemaVersion": 1,
              "Machines": [
                { "Id": "11111111-1111-1111-1111-111111111111", "Name": "NAS", "Mac": "AA:BB:CC:DD:EE:FF", "Ipv4BroadcastAddress": "192.168.1.255", "Port": 9 }
              ]
            }
            """);
        var store = new TargetMachineStore(storage);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TargetMachinesLoadStatus.Success, result.Status);
        var machine = Assert.Single(result.Document!.Machines);
        Assert.Equal("NAS", machine.Name);
        Assert.Equal("192.168.1.255", machine.Ipv4BroadcastAddress);
        Assert.Equal(9, machine.Port);
    }

    [Fact]
    public async Task Save_WhenValid_WritesToAtomicStorage()
    {
        var storage = new InMemoryStorage();
        var store = new TargetMachineStore(storage);

        var result = await store.SaveAsync(
            new TargetMachinesDocument { Machines = [ValidMachine()] },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(FileName, storage.WritePaths[0]);
    }

    [Fact]
    public async Task Save_WhenWriteFails_ReturnsIoFailure()
    {
        var storage = new InMemoryStorage { WriteStatus = StorageWriteStatus.IoFailure };
        var store = new TargetMachineStore(storage);

        var result = await store.SaveAsync(
            new TargetMachinesDocument { Machines = [ValidMachine()] },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TargetMachinesSaveStatus.IoFailure, result.Status);
    }

    [Fact]
    public async Task Save_WhenInvalidMachine_ReturnsInvalidAndWritesNothing()
    {
        var storage = new InMemoryStorage();
        var store = new TargetMachineStore(storage);

        var result = await store.SaveAsync(
            new TargetMachinesDocument
            {
                Machines =
                [
                    new WakeOnLanTarget { Id = Guid.NewGuid(), Name = "X", Mac = "invalid" }
                ]
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TargetMachinesSaveStatus.Invalid, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    private static WakeOnLanTarget ValidMachine() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "NAS",
        Mac = "AA:BB:CC:DD:EE:FF"
    };

    internal sealed class InMemoryStorage : IStorage
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
