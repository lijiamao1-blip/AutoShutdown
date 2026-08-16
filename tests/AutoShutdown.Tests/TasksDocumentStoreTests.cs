using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class TasksDocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AutoShutdown.Tests",
        Guid.NewGuid().ToString("N"));

    // 与 S13-work包/tasks.json.sample 内容一致的 V1 样例（S14 迁移测试用，含版本字段）。
    private const string V1TasksJson =
        """{"SchemaVersion":1,"Tasks":[{"Id":"11111111-1111-1111-1111-111111111111","Kind":1,"Action":1,"CountdownDuration":"02:00:00","TargetTimeOfDay":null,"WarningSeconds":60,"CreatedAt":"2024-01-15T10:00:00+00:00","RealPowerConfirmed":false,"IsEnabled":true,"Priority":0},{"Id":"22222222-2222-2222-2222-222222222222","Kind":3,"Action":2,"CountdownDuration":null,"TargetTimeOfDay":"20:00:00","WarningSeconds":300,"CreatedAt":"2024-01-15T10:00:00+00:00","RealPowerConfirmed":false,"IsEnabled":true,"Priority":50}]}""";

    // 当前 V2 样例：与 V1TasksJson 同任务集，仅 SchemaVersion 提升为当前版本。
    private const string ValidTasksJson =
        """{"SchemaVersion":2,"Tasks":[{"Id":"11111111-1111-1111-1111-111111111111","Kind":1,"Action":1,"CountdownDuration":"02:00:00","TargetTimeOfDay":null,"WarningSeconds":60,"CreatedAt":"2024-01-15T10:00:00+00:00","RealPowerConfirmed":false,"IsEnabled":true,"Priority":0},{"Id":"22222222-2222-2222-2222-222222222222","Kind":3,"Action":2,"CountdownDuration":null,"TargetTimeOfDay":"20:00:00","WarningSeconds":300,"CreatedAt":"2024-01-15T10:00:00+00:00","RealPowerConfirmed":false,"IsEnabled":true,"Priority":50}]}""";

    private const string CountdownTaskJson =
        """{"Id":"11111111-1111-1111-1111-111111111111","Kind":1,"Action":1,"CountdownDuration":"02:00:00","TargetTimeOfDay":null,"WarningSeconds":60,"CreatedAt":"2024-01-15T10:00:00+00:00","RealPowerConfirmed":false,"IsEnabled":true,"Priority":0}""";

    [Fact]
    public async Task LoadAsync_WhenFileMissing_ReturnsNotFound()
    {
        var store = CreateStore();

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.NotFound, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsCorrupt_ReturnsCorruptWithoutFallback()
    {
        await SeedAsync("{ incomplete");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Corrupt, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadAsync_WhenValid_ReturnsDocument()
    {
        await SeedAsync(ValidTasksJson);

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Success, result.Status);
        Assert.NotNull(result.Document);
        Assert.Equal(TasksDocument.CurrentSchemaVersion, result.Document!.SchemaVersion);
        Assert.Equal(2, result.Document.Tasks.Count);
        Assert.Equal(TaskKind.Countdown, result.Document.Tasks[0].Kind);
        Assert.Equal(TimeSpan.FromHours(2), result.Document.Tasks[0].CountdownDuration);
        Assert.Equal(TaskKind.DailyAt, result.Document.Tasks[1].Kind);
        Assert.Equal(new TimeOnly(20, 0), result.Document.Tasks[1].TargetTimeOfDay);
        Assert.Equal(50, result.Document.Tasks[1].Priority);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task LoadAsync_WhenV1_ReturnsMigrated_AndUpgradesToCurrentVersion()
    {
        await SeedAsync(V1TasksJson);

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Migrated, result.Status);
        Assert.NotNull(result.Document);
        Assert.Equal(TasksDocument.CurrentSchemaVersion, result.Document!.SchemaVersion);
        Assert.Equal(2, result.Document.Tasks.Count);
        Assert.Equal(TaskKind.Countdown, result.Document.Tasks[0].Kind);
        Assert.Equal(TimeSpan.FromHours(2), result.Document.Tasks[0].CountdownDuration);
        Assert.Equal(TaskKind.DailyAt, result.Document.Tasks[1].Kind);
        Assert.Equal(new TimeOnly(20, 0), result.Document.Tasks[1].TargetTimeOfDay);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionMissing_ReturnsInvalid()
    {
        await SeedAsync("""{"Tasks":[]}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Invalid, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionNewer_ReturnsUnsupportedVersion()
    {
        await SeedAsync("""{"SchemaVersion":3,"Tasks":[]}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadAsync_WhenRootIsNotAnObject_ReturnsInvalid()
    {
        await SeedAsync("[1,2]");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Invalid, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadAsync_WhenTaskStructurallyInvalid_ReturnsInvalid()
    {
        // Countdown 缺少时长：结构无效数据不得被静默当作空任务集。
        await SeedAsync(
            """{"SchemaVersion":1,"Tasks":[{"Id":"11111111-1111-1111-1111-111111111111","Kind":1,"Action":1,"CountdownDuration":null,"TargetTimeOfDay":null,"WarningSeconds":60,"CreatedAt":"2024-01-15T10:00:00+00:00","RealPowerConfirmed":false,"IsEnabled":true,"Priority":0}]}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Invalid, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadAsync_WhenTaskItemIsNull_ReturnsInvalid()
    {
        await SeedAsync("""{"SchemaVersion":1,"Tasks":[null]}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Invalid, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LoadAsync_WhenTaskIdsAreDuplicated_ReturnsInvalid()
    {
        await SeedAsync(
            $$"""{"SchemaVersion":1,"Tasks":[{{CountdownTaskJson}},{{CountdownTaskJson}}]}""");

        var result = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Invalid, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task SaveAsync_WhenValid_WritesAndRoundTrips()
    {
        var store = CreateStore();
        var document = new TasksDocument
        {
            Tasks =
            [
                new TaskDefinition
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Kind = TaskKind.Countdown,
                    Action = PowerAction.Shutdown,
                    CountdownDuration = TimeSpan.FromHours(2),
                    WarningSeconds = 60,
                    CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
                    Priority = 0
                }
            ]
        };

        var save = await store.SaveAsync(document, CancellationToken.None);

        Assert.True(save.Succeeded);
        Assert.Equal(TasksSaveStatus.Success, save.Status);

        var read = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(TasksLoadStatus.Success, read.Status);
        Assert.Single(read.Document!.Tasks);
        Assert.Equal(TimeSpan.FromHours(2), read.Document.Tasks[0].CountdownDuration);
    }

    [Fact]
    public async Task SaveAsync_WhenS14ComplexRules_WritesAndRoundTrips()
    {
        var store = CreateStore();
        var weekdaysId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var nextWorkdayId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var nthWorkdayId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var oneTimeId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var document = new TasksDocument
        {
            Tasks =
            [
                new TaskDefinition
                {
                    Id = weekdaysId,
                    Kind = TaskKind.Weekdays,
                    Action = PowerAction.Shutdown,
                    TargetTimeOfDay = new TimeOnly(9, 30),
                    Weekdays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
                    HolidayDates = [new DateOnly(2024, 5, 1)],
                    CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
                    Priority = 10
                },
                new TaskDefinition
                {
                    Id = nextWorkdayId,
                    Kind = TaskKind.NextWorkday,
                    Action = PowerAction.Restart,
                    TargetTimeOfDay = new TimeOnly(8, 0),
                    HolidayDates = [new DateOnly(2024, 1, 1)],
                    CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
                },
                new TaskDefinition
                {
                    Id = nthWorkdayId,
                    Kind = TaskKind.NthWorkdayOfMonth,
                    Action = PowerAction.Sleep,
                    TargetTimeOfDay = new TimeOnly(18, 15),
                    NthWorkday = 5,
                    CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
                },
                new TaskDefinition
                {
                    Id = oneTimeId,
                    Kind = TaskKind.OneTime,
                    Action = PowerAction.Hibernate,
                    OneTimeDateTime = new DateTime(2024, 12, 24, 22, 0, 0),
                    CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
                }
            ]
        };

        var save = await store.SaveAsync(document, CancellationToken.None);

        Assert.True(save.Succeeded);
        Assert.Equal(TasksSaveStatus.Success, save.Status);

        var read = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(TasksLoadStatus.Success, read.Status);
        Assert.Equal(4, read.Document!.Tasks.Count);

        var weekdays = read.Document.Tasks.Single(task => task.Id == weekdaysId);
        Assert.Equal(TaskKind.Weekdays, weekdays.Kind);
        Assert.Equal(new TimeOnly(9, 30), weekdays.TargetTimeOfDay);
        Assert.Equal(
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
            weekdays.Weekdays);
        Assert.Equal(new[] { new DateOnly(2024, 5, 1) }, weekdays.HolidayDates);

        var nextWorkday = read.Document.Tasks.Single(task => task.Id == nextWorkdayId);
        Assert.Equal(TaskKind.NextWorkday, nextWorkday.Kind);
        Assert.Equal(new[] { new DateOnly(2024, 1, 1) }, nextWorkday.HolidayDates);

        var nthWorkday = read.Document.Tasks.Single(task => task.Id == nthWorkdayId);
        Assert.Equal(TaskKind.NthWorkdayOfMonth, nthWorkday.Kind);
        Assert.Equal(5, nthWorkday.NthWorkday);
        Assert.Equal(new TimeOnly(18, 15), nthWorkday.TargetTimeOfDay);

        var oneTime = read.Document.Tasks.Single(task => task.Id == oneTimeId);
        Assert.Equal(TaskKind.OneTime, oneTime.Kind);
        Assert.Equal(new DateTime(2024, 12, 24, 22, 0, 0), oneTime.OneTimeDateTime);
    }

    [Fact]
    public async Task SaveAsync_WhenDocumentStructurallyInvalid_DoesNotWrite()
    {
        var store = CreateStore();
        var document = new TasksDocument
        {
            Tasks =
            [
                new TaskDefinition
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Kind = TaskKind.Countdown,
                    Action = PowerAction.Shutdown,
                    CountdownDuration = null,
                    CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
                }
            ]
        };

        var save = await store.SaveAsync(document, CancellationToken.None);

        Assert.Equal(TasksSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, TasksDocumentStore.FileName)));
    }

    [Fact]
    public async Task SaveAsync_WhenSchemaVersionWrong_ReturnsInvalid()
    {
        var store = CreateStore();
        var document = new TasksDocument { SchemaVersion = 1 };

        var save = await store.SaveAsync(document, CancellationToken.None);

        Assert.Equal(TasksSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task SaveAsync_WhenTaskItemIsNull_DoesNotWrite()
    {
        var store = CreateStore();
        var document = new TasksDocument
        {
            Tasks = new TaskDefinition[] { null! }
        };

        var save = await store.SaveAsync(document, CancellationToken.None);

        Assert.Equal(TasksSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, TasksDocumentStore.FileName)));
    }

    [Fact]
    public async Task SaveAsync_WhenTaskIdsAreDuplicated_DoesNotWrite()
    {
        var store = CreateStore();
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var document = new TasksDocument
        {
            Tasks =
            [
                ValidCountdown(id),
                ValidCountdown(id)
            ]
        };

        var save = await store.SaveAsync(document, CancellationToken.None);

        Assert.Equal(TasksSaveStatus.Invalid, save.Status);
        Assert.False(save.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, TasksDocumentStore.FileName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TasksDocumentStore CreateStore() => new(new FileStorage(_root));

    private static TaskDefinition ValidCountdown(Guid id) => new()
    {
        Id = id,
        Kind = TaskKind.Countdown,
        Action = PowerAction.Shutdown,
        CountdownDuration = TimeSpan.FromHours(2),
        WarningSeconds = 60,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
        Priority = 0
    };

    private async Task SeedAsync(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, TasksDocumentStore.FileName),
            json,
            CancellationToken.None);
    }
}
