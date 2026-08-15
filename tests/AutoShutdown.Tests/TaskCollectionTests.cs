using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// 任务定义集合化领域模型测试（S13-T02A）：多任务增删改查、分别启停、
/// Priority（GATE-Q4）/Action/Id 校验，无任何 tasks.json 读写。
/// </summary>
public sealed class TaskCollectionTests
{
    private static readonly Guid TaskA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TaskC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ===== 多任务增删改查 =====

    [Fact]
    public void Add_MultipleTasks_IncrementsCountAndReturnsThem()
    {
        var collection = new TaskCollection();

        var a = collection.Add(Definition(TaskA));
        var b = collection.Add(Definition(TaskB));
        var c = collection.Add(Definition(TaskC));

        Assert.True(a.Succeeded);
        Assert.True(b.Succeeded);
        Assert.True(c.Succeeded);
        Assert.Equal(3, collection.Count);
        Assert.Equal(3, collection.Items.Count);
        Assert.Contains(collection.Items, d => d.Id == TaskA);
        Assert.Contains(collection.Items, d => d.Id == TaskB);
        Assert.Contains(collection.Items, d => d.Id == TaskC);
    }

    [Fact]
    public void Add_DuplicateId_ReturnsDuplicateIdAndDoesNotMutate()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(TaskA));

        var duplicate = collection.Add(Definition(TaskA));

        Assert.False(duplicate.Succeeded);
        Assert.Equal(TaskCollectionStatus.DuplicateId, duplicate.Status);
        Assert.Equal(1, collection.Count);
    }

    [Fact]
    public void Update_ExistingId_ReplacesDefinition()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(TaskA));

        var updated = collection.Update(Definition(TaskA, priority: 50, isEnabled: false));

        Assert.True(updated.Succeeded);
        Assert.Equal(50, updated.Definition!.Priority);
        Assert.False(updated.Definition.IsEnabled);
        Assert.Equal(1, collection.Count);
        Assert.Equal(50, collection.Get(TaskA)!.Priority);
        Assert.False(collection.Get(TaskA)!.IsEnabled);
    }

    [Fact]
    public void Update_NonExistentId_ReturnsNotFound()
    {
        var collection = new TaskCollection();

        var result = collection.Update(Definition(TaskA));

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.NotFound, result.Status);
        Assert.Equal(0, collection.Count);
    }

    [Fact]
    public void Remove_ExistingId_RemovesAndDecrementsCount()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(TaskA));
        collection.Add(Definition(TaskB));

        var result = collection.Remove(TaskA);

        Assert.True(result.Succeeded);
        Assert.Equal(1, collection.Count);
        Assert.Null(collection.Get(TaskA));
        Assert.NotNull(collection.Get(TaskB));
    }

    [Fact]
    public void Remove_NonExistentId_ReturnsNotFound()
    {
        var collection = new TaskCollection();

        var result = collection.Remove(TaskA);

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.NotFound, result.Status);
    }

    [Fact]
    public void Get_TryGet_Contains_ReflectMembership()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(TaskA));

        Assert.NotNull(collection.Get(TaskA));
        Assert.Null(collection.Get(TaskB));

        Assert.True(collection.TryGet(TaskA, out var found));
        Assert.Equal(TaskA, found!.Id);
        Assert.False(collection.TryGet(TaskB, out _));

        Assert.True(collection.Contains(TaskA));
        Assert.False(collection.Contains(TaskB));
    }

    // ===== 分别启停 =====

    [Fact]
    public void SetEnabled_ExistingId_TogglesIsEnabled()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(TaskA));

        Assert.True(collection.Get(TaskA)!.IsEnabled);

        var disabled = collection.SetEnabled(TaskA, false);
        Assert.True(disabled.Succeeded);
        Assert.False(collection.Get(TaskA)!.IsEnabled);
        Assert.False(disabled.Definition!.IsEnabled);

        var enabled = collection.SetEnabled(TaskA, true);
        Assert.True(enabled.Succeeded);
        Assert.True(collection.Get(TaskA)!.IsEnabled);
    }

    [Fact]
    public void SetEnabled_NonExistentId_ReturnsNotFound()
    {
        var collection = new TaskCollection();

        var result = collection.SetEnabled(TaskA, false);

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.NotFound, result.Status);
    }

    [Fact]
    public void Definition_DefaultsIsEnabledTrueAndPriorityZero()
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA));

        Assert.True(result.Succeeded);
        Assert.True(result.Definition!.IsEnabled);
        Assert.Equal(0, result.Definition.Priority);
    }

    // ===== Priority 校验（GATE-Q4） =====

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void Add_PriorityWithinRange_IsAccepted(int priority)
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA, priority: priority));

        Assert.True(result.Succeeded);
        Assert.Equal(priority, result.Definition!.Priority);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Add_PriorityOutOfRange_ReturnsInvalidDefinitionWithoutMutation(int priority)
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA, priority: priority));

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(0, collection.Count);
        Assert.Null(collection.Get(TaskA));
    }

    [Fact]
    public void Update_PriorityOutOfRange_ReturnsInvalidDefinitionAndKeepsOriginal()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(TaskA, priority: 20));

        var result = collection.Update(Definition(TaskA, priority: 150));

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(20, collection.Get(TaskA)!.Priority);
    }

    // ===== 其他字段校验（保留现有校验语义） =====

    [Fact]
    public void Add_EmptyId_ReturnsInvalidDefinition()
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(Guid.Empty));

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(0, collection.Count);
    }

    [Theory]
    [InlineData(PowerAction.Unknown)]
    [InlineData((PowerAction)99)]
    public void Add_InvalidAction_ReturnsInvalidDefinition(PowerAction action)
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA, action: action));

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
    }

    [Fact]
    public void Remove_EmptyId_ReturnsInvalidDefinition()
    {
        var collection = new TaskCollection();

        var result = collection.Remove(Guid.Empty);

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
    }

    // ===== 结构校验（S13-T02A 补丁）：TaskKind / CountdownDuration / TargetTimeOfDay / WarningSeconds =====

    [Theory]
    [InlineData(TaskKind.Unknown)]
    [InlineData((TaskKind)99)]
    public void Add_UnknownOrUndefinedTaskKind_ReturnsInvalidDefinitionWithoutMutation(TaskKind kind)
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA) with { Kind = kind });

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(0, collection.Count);
    }

    [Fact]
    public void Add_CountdownMissingDuration_ReturnsInvalidDefinition()
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA) with { CountdownDuration = null });

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(0, collection.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Add_CountdownNonPositiveDuration_ReturnsInvalidDefinition(int seconds)
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA) with { CountdownDuration = TimeSpan.FromSeconds(seconds) });

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(0, collection.Count);
    }

    [Theory]
    [InlineData(TaskKind.TodayAt)]
    [InlineData(TaskKind.DailyAt)]
    public void Add_TimeOfDayTaskMissingTarget_ReturnsInvalidDefinition(TaskKind kind)
    {
        var collection = new TaskCollection();

        var result = collection.Add(TimeOfDayDefinition(TaskA, kind, targetTimeOfDay: null));

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(0, collection.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86401)]
    public void Add_WarningSecondsOutOfRange_ReturnsInvalidDefinition(int warningSeconds)
    {
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA) with { WarningSeconds = warningSeconds });

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(0, collection.Count);
    }

    [Fact]
    public void Update_StructurallyInvalidDefinition_KeepsOriginal()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(TaskA, priority: 20));

        var result = collection.Update(Definition(TaskA) with { Kind = TaskKind.Unknown });

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCollectionStatus.InvalidDefinition, result.Status);
        Assert.Equal(20, collection.Get(TaskA)!.Priority);
        Assert.Equal(TaskKind.Countdown, collection.Get(TaskA)!.Kind);
    }

    [Fact]
    public void Add_AllThreeValidTaskKinds_AreAccepted()
    {
        var collection = new TaskCollection();

        var countdown = collection.Add(Definition(TaskA));
        var todayAt = collection.Add(TimeOfDayDefinition(TaskB, TaskKind.TodayAt, new TimeOnly(20, 0)));
        var dailyAt = collection.Add(TimeOfDayDefinition(TaskC, TaskKind.DailyAt, new TimeOnly(8, 30)));

        Assert.True(countdown.Succeeded);
        Assert.True(todayAt.Succeeded);
        Assert.True(dailyAt.Succeeded);
        Assert.Equal(3, collection.Count);
    }

    // ===== TaskService 委托（集合化 CRUD 通过 ITaskService 暴露） =====

    [Fact]
    public void TaskService_ExposesCollectionCrud_DelegatingToTaskCollection()
    {
        var service = CreateService();

        var add = service.Add(Definition(TaskA, priority: 30));
        Assert.True(add.Succeeded);
        Assert.Equal(30, service.Get(TaskA)!.Priority);

        var all = service.GetAll();
        Assert.Single(all);
        Assert.Equal(TaskA, all.First().Id);

        var disable = service.SetEnabled(TaskA, false);
        Assert.True(disable.Succeeded);
        Assert.False(service.Get(TaskA)!.IsEnabled);

        var remove = service.Remove(TaskA);
        Assert.True(remove.Succeeded);
        Assert.Null(service.Get(TaskA));
        Assert.Empty(service.GetAll());
    }

    [Fact]
    public void TaskService_CollectionCrud_IsIsolatedPerInstance()
    {
        var first = CreateService();
        var second = CreateService();

        first.Add(Definition(TaskA));
        second.Add(Definition(TaskB));

        Assert.Single(first.GetAll());
        Assert.Single(second.GetAll());
        Assert.Equal(TaskA, first.GetAll().First().Id);
        Assert.Equal(TaskB, second.GetAll().First().Id);
    }

    // ===== 本卡无持久化：无 tasks.json 读写 =====

    [Fact]
    public void Collection_IsInMemoryOnly_HasNoPersistenceDependency()
    {
        // 参数less 构造即证明无注入的 storage/configuration 依赖。
        var collection = new TaskCollection();

        var result = collection.Add(Definition(TaskA));

        Assert.True(result.Succeeded);
        Assert.Equal(1, collection.Count);
    }

    private static TaskService CreateService() => new(
        new AutoShutdown.Core.Scheduling.NextExecutionCalculator(),
        new AutoShutdown.Core.State.TaskStateMachine(),
        new AutoShutdown.Core.Tasks.GuidIdentifierGenerator());

    private static TaskDefinition Definition(
        Guid id,
        PowerAction action = PowerAction.Shutdown,
        int priority = 0,
        bool isEnabled = true) => new()
        {
            Id = id,
            Kind = TaskKind.Countdown,
            Action = action,
            CountdownDuration = TimeSpan.FromHours(2),
            WarningSeconds = 60,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            Priority = priority,
            IsEnabled = isEnabled
        };

    private static TaskDefinition TimeOfDayDefinition(
        Guid id,
        TaskKind kind,
        TimeOnly? targetTimeOfDay) => new()
        {
            Id = id,
            Kind = kind,
            Action = PowerAction.Shutdown,
            TargetTimeOfDay = targetTimeOfDay,
            WarningSeconds = 60,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };
}
