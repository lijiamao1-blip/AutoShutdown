using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.WakeOnLan;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>S21-C1：目标机器增删改查；损坏/非法文档 fail-closed 阻止写入；首次写入从空清单开始。</summary>
public sealed class S21_TargetMachineManagerTests
{
    private const string FileName = TargetMachineStore.FileName;

    [Fact]
    public async Task Add_WhenNoFileExists_FirstWriteSucceeds()
    {
        var manager = CreateManager();

        var result = await manager.AddAsync(ValidMachine("NAS"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(await manager.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Add_ThenGet_ReturnsMachine()
    {
        var manager = CreateManager();
        var machine = ValidMachine("NAS", mac: "AA:BB:CC:DD:EE:FF", broadcast: "192.168.1.255", port: 9);

        await manager.AddAsync(machine, CancellationToken.None);

        var loaded = await manager.GetAsync(machine.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("NAS", loaded!.Name);
        Assert.Equal("192.168.1.255", loaded.Ipv4BroadcastAddress);
        Assert.Equal(9, loaded.Port);
    }

    [Fact]
    public async Task Add_WhenDuplicateId_ReturnsDuplicateIdAndWritesNothing()
    {
        var manager = CreateManager();
        var machine = ValidMachine("NAS");
        await manager.AddAsync(machine, CancellationToken.None);

        var result = await manager.AddAsync(machine with { Name = "Dupe" }, CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.DuplicateId, result.Status);
        Assert.False(result.Succeeded);
        Assert.Single(await manager.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Add_WhenInvalid_ReturnsInvalidAndWritesNothing()
    {
        var manager = CreateManager();

        var result = await manager.AddAsync(
            ValidMachine("Bad") with { Mac = "not-a-mac" },
            CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.Invalid, result.Status);
        Assert.Empty(await manager.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Update_WhenExists_PersistsChange()
    {
        var manager = CreateManager();
        var machine = ValidMachine("NAS", mac: "AA:BB:CC:DD:EE:FF");
        await manager.AddAsync(machine, CancellationToken.None);

        var result = await manager.UpdateAsync(
            machine with { Mac = "AA:BB:CC:DD:EE:00" },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var loaded = await manager.GetAsync(machine.Id, CancellationToken.None);
        Assert.Equal("AA:BB:CC:DD:EE:00", loaded!.Mac);
    }

    [Fact]
    public async Task Update_WhenMissing_ReturnsNotFound()
    {
        var manager = CreateManager();

        var result = await manager.UpdateAsync(ValidMachine("Ghost"), CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Remove_WhenExists_RemovesMachine()
    {
        var manager = CreateManager();
        var machine = ValidMachine("NAS");
        await manager.AddAsync(machine, CancellationToken.None);

        var result = await manager.RemoveAsync(machine.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(await manager.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Remove_WhenMissing_ReturnsNotFound()
    {
        var manager = CreateManager();

        var result = await manager.RemoveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Mutation_WhenCorruptDocument_ReturnsLoadUnavailableAndWritesNothing()
    {
        var storage = new RoundTripStorage { ReadStatus = StorageReadStatus.Corrupt };
        var manager = new TargetMachineManager(new TargetMachineStore(storage));

        var result = await manager.AddAsync(ValidMachine("NAS"), CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.LoadUnavailable, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task Mutation_WhenUnsupportedVersion_ReturnsLoadUnavailable()
    {
        var storage = new RoundTripStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 999, "Machines": [] }""");
        var manager = new TargetMachineManager(new TargetMachineStore(storage));

        var result = await manager.AddAsync(ValidMachine("NAS"), CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.LoadUnavailable, result.Status);
    }

    [Fact]
    public async Task Mutation_WhenInvalidDocument_ReturnsLoadUnavailableAndNeverOverwrites()
    {
        var storage = new RoundTripStorage();
        storage.Seed(FileName, """{ "SchemaVersion": 1, "Machines": [ { "Id": "00000000-0000-0000-0000-000000000000", "Name": "X", "Mac": "bad" } ] }""");
        var manager = new TargetMachineManager(new TargetMachineStore(storage));

        var result = await manager.AddAsync(ValidMachine("NAS"), CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.LoadUnavailable, result.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task GetAll_WhenCorrupt_ReturnsEmptyList()
    {
        var storage = new RoundTripStorage { ReadStatus = StorageReadStatus.Corrupt };
        var manager = new TargetMachineManager(new TargetMachineStore(storage));

        var machines = await manager.GetAllAsync(CancellationToken.None);

        Assert.Empty(machines);
    }

    [Fact]
    public async Task Add_WhenWriteFails_ReturnsIoFailure()
    {
        var storage = new RoundTripStorage { WriteStatus = StorageWriteStatus.IoFailure };
        var manager = new TargetMachineManager(new TargetMachineStore(storage));

        var result = await manager.AddAsync(ValidMachine("NAS"), CancellationToken.None);

        Assert.Equal(TargetMachineMutationStatus.IoFailure, result.Status);
        Assert.False(result.Succeeded);
    }

    private static TargetMachineManager CreateManager()
        => new(new TargetMachineStore(new RoundTripStorage()));

    private static WakeOnLanTarget ValidMachine(
        string name,
        string mac = "AA:BB:CC:DD:EE:FF",
        string? broadcast = null,
        int? port = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Mac = mac,
        Ipv4BroadcastAddress = broadcast,
        Port = port
    };

    /// <summary>写入会持久化到内存字典，后续读取可见（用于 CRUD 往返断言）。</summary>
    private sealed class RoundTripStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _documents = new();

        public StorageReadStatus ReadStatus { get; init; } = StorageReadStatus.Success;

        public StorageWriteStatus WriteStatus { get; init; } = StorageWriteStatus.Success;

        public int WriteCount { get; private set; }

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

            WriteCount++;
            _documents[relativePath] = JsonSerializer.SerializeToElement(value!);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
        }
    }
}
