using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class RuntimeStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AutoShutdown.Tests",
        Guid.NewGuid().ToString("N"));

    private static readonly Guid TaskA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstanceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid InstanceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TokenA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid TokenB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task LoadAsync_WhenFileMissing_ReturnsNotFound()
    {
        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.NotFound, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsCorrupt_ReturnsCorruptWithoutFallback()
    {
        await SeedAsync("{ incomplete");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Corrupt, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenValidMultiInstance_ReturnsState()
    {
        await SeedAsync(ValidV2Json);

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Success, result.Status);
        Assert.NotNull(result.State);
        Assert.Equal(2, result.State!.SchemaVersion);
        Assert.Equal(2, result.State.Instances.Count);
        Assert.True(result.State.Instances.ContainsKey(TaskA));
        Assert.True(result.State.Instances.ContainsKey(TaskB));
        Assert.Equal(TaskInstanceState.Waiting, result.State.Instances[TaskA].State);
        Assert.Equal(PowerAction.Shutdown, result.State.Instances[TaskA].ActionSnapshot);
        Assert.Equal(TokenA, result.State.Instances[TaskA].StageToken);
        Assert.Equal(TaskInstanceState.Confirming, result.State.Instances[TaskB].State);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionMissing_ReturnsInvalid()
    {
        await SeedAsync("""{"Instances":{}}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Invalid, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionNewer_ReturnsUnsupportedVersion()
    {
        await SeedAsync("""{"SchemaVersion":3,"Instances":{}}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersion1WithoutCurrentInstance_ReturnsInvalid()
    {
        // 既非 V1 单实例（无 CurrentInstance）又非 V2（version != 2）→ 无迁移链，拒绝。
        await SeedAsync("""{"SchemaVersion":1,"Instances":{}}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Invalid, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenRootIsNotAnObject_ReturnsInvalid()
    {
        await SeedAsync("[1,2]");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Invalid, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenInstanceKeyMismatchesSourceTaskId_ReturnsInvalid()
    {
        // 字典键（task id）必须等于 TaskInstance.SourceTaskId。
        await SeedAsync(
            $$"""{"SchemaVersion":2,"Instances":{"99999999-9999-9999-9999-999999999999":{{InstanceJson(TaskA, InstanceA, TokenA, TaskInstanceState.Waiting)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Invalid, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenInstanceStateUnknown_ReturnsInvalid()
    {
        await SeedAsync(
            $$"""{"SchemaVersion":2,"Instances":{"{{TaskA:D}}":{{InstanceJson(TaskA, InstanceA, TokenA, TaskInstanceState.Unknown)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Invalid, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task LoadAsync_WhenV1SingleInstance_BacksUpAndRebuildsEmpty()
    {
        await SeedAsync(V1Json);

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(RuntimeStateLoadStatus.Migrated, result.Status);
        Assert.NotNull(result.State);
        Assert.Empty(result.State!.Instances);
        Assert.Equal(RuntimeState.CurrentSchemaVersion, result.State.SchemaVersion);
        Assert.Equal(RuntimeStateStore.V1BackupFileName, result.BackupPath);

        // 旧 V1 单实例被备份为 .v1bak，未伪造 task id、未残留单实例。
        var backupPath = Path.Combine(_root, RuntimeStateStore.V1BackupFileName);
        Assert.True(File.Exists(backupPath));
        var backupText = await File.ReadAllTextAsync(backupPath);
        Assert.Contains("CurrentInstance", backupText);

        // runtime.json 已重建为空 V2 多实例态。
        var runtimeText = await File.ReadAllTextAsync(Path.Combine(_root, RuntimeStateStore.FileName));
        Assert.Contains("\"SchemaVersion\": 2", runtimeText);
        Assert.Contains("Instances", runtimeText);
        Assert.DoesNotContain("CurrentInstance", runtimeText);
    }

    [Fact]
    public async Task SaveAsync_WhenValid_WritesAndRoundTrips()
    {
        var store = CreateStore();
        var state = new RuntimeState
        {
            SchemaVersion = RuntimeState.CurrentSchemaVersion,
            Instances = new Dictionary<Guid, TaskInstance>
            {
                [TaskA] = ValidInstance(TaskA, InstanceA, TokenA, TaskInstanceState.Waiting)
            },
            LastUpdatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };

        var save = await store.SaveAsync(state, CancellationToken.None);

        Assert.True(save.Succeeded);
        Assert.Equal(RuntimeStateSaveStatus.Success, save.Status);

        var read = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(RuntimeStateLoadStatus.Success, read.Status);
        Assert.Single(read.State!.Instances);
        Assert.Equal(TaskInstanceState.Waiting, read.State.Instances[TaskA].State);
        Assert.Equal(TokenA, read.State.Instances[TaskA].StageToken);
    }

    [Fact]
    public async Task SaveAsync_WhenInstanceKeyMismatchesSourceTaskId_DoesNotWrite()
    {
        var store = CreateStore();
        var state = new RuntimeState
        {
            Instances = new Dictionary<Guid, TaskInstance>
            {
                [TaskB] = ValidInstance(TaskA, InstanceA, TokenA, TaskInstanceState.Waiting)
            }
        };

        var save = await store.SaveAsync(state, CancellationToken.None);

        Assert.Equal(RuntimeStateSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, RuntimeStateStore.FileName)));
    }

    [Fact]
    public async Task SaveAsync_WhenInstanceStateUnknown_DoesNotWrite()
    {
        var store = CreateStore();
        var state = new RuntimeState
        {
            Instances = new Dictionary<Guid, TaskInstance>
            {
                [TaskA] = ValidInstance(TaskA, InstanceA, TokenA, TaskInstanceState.Unknown)
            }
        };

        var save = await store.SaveAsync(state, CancellationToken.None);

        Assert.Equal(RuntimeStateSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, RuntimeStateStore.FileName)));
    }

    [Fact]
    public async Task SaveAsync_WhenInstanceHasEmptyStageToken_DoesNotWrite()
    {
        var store = CreateStore();
        var state = new RuntimeState
        {
            Instances = new Dictionary<Guid, TaskInstance>
            {
                [TaskA] = ValidInstance(TaskA, InstanceA, Guid.Empty, TaskInstanceState.Waiting)
            }
        };

        var save = await store.SaveAsync(state, CancellationToken.None);

        Assert.Equal(RuntimeStateSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task SaveAsync_WhenSchemaVersionWrong_ReturnsInvalid()
    {
        var store = CreateStore();
        var state = new RuntimeState { SchemaVersion = 1 };

        var save = await store.SaveAsync(state, CancellationToken.None);

        Assert.Equal(RuntimeStateSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private RuntimeStateStore CreateStore() => new(new FileStorage(_root));

    private static TaskInstance ValidInstance(
        Guid taskId,
        Guid instanceId,
        Guid stageToken,
        TaskInstanceState state) => new()
        {
            InstanceId = instanceId,
            SourceTaskId = taskId,
            ActionSnapshot = PowerAction.Shutdown,
            State = state,
            ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            StageToken = stageToken,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero)
        };

    private static string InstanceJson(
        Guid taskId,
        Guid instanceId,
        Guid stageToken,
        TaskInstanceState state) =>
        $$"""{"InstanceId":"{{instanceId:D}}","SourceTaskId":"{{taskId:D}}","ActionSnapshot":1,"State":{{(int)state}},"ScheduledFireTime":"2024-01-15T10:00:00+00:00","WarningStartTime":null,"StageToken":"{{stageToken:D}}","HasExecuted":false,"CreatedAt":"2024-01-15T09:00:00+00:00","RealPowerConfirmed":false,"RealPowerConfirmationId":null}""";

    private string ValidV2Json =>
        $$"""{"SchemaVersion":2,"Instances":{"{{TaskA:D}}":{{InstanceJson(TaskA, InstanceA, TokenA, TaskInstanceState.Waiting)}},"{{TaskB:D}}":{{InstanceJson(TaskB, InstanceB, TokenB, TaskInstanceState.Confirming)}}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private const string V1Json =
        """{"SchemaVersion":1,"CurrentInstance":{"InstanceId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","SourceTaskId":"11111111-1111-1111-1111-111111111111","ActionSnapshot":1,"State":2,"ScheduledFireTime":"2024-01-15T10:00:00+00:00","WarningStartTime":null,"StageToken":"cccccccc-cccc-cccc-cccc-cccccccccccc","HasExecuted":false,"CreatedAt":"2024-01-15T09:00:00+00:00","RealPowerConfirmed":false,"RealPowerConfirmationId":null},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private async Task SeedAsync(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, RuntimeStateStore.FileName),
            json,
            CancellationToken.None);
    }
}
