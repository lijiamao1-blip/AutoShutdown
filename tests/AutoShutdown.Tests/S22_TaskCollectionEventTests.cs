using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 CP3 测试：TaskCollection 变更事件（新增/更新/删除/分别启停）只在实际提交成功后触发；
/// 失败操作不触发；TaskService 通过 CollectionChanged 透传给消费方。
/// </summary>
public sealed class S22_TaskCollectionEventTests
{
    // ---- 成功操作触发对应事件 ----

    [Fact]
    public void Add_RaisesAdded_WithLatestDefinition()
    {
        var collection = new TaskCollection();
        var events = new List<TaskCollectionChangedEventArgs>();
        collection.Changed += (_, args) => events.Add(args);

        var task = Definition();
        var result = collection.Add(task);

        Assert.True(result.Succeeded);
        var args = Assert.Single(events);
        Assert.Equal(TaskCollectionChangeKind.Added, args.Kind);
        Assert.Equal(task.Id, args.TaskId);
        Assert.Same(task, args.Definition);
    }

    [Fact]
    public void Update_RaisesUpdated_WithLatestDefinition()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(id: TaskA, timeOfDay: new TimeOnly(9, 0, 0)));
        var events = new List<TaskCollectionChangedEventArgs>();
        collection.Changed += (_, args) => events.Add(args);

        var updated = Definition(id: TaskA, timeOfDay: new TimeOnly(17, 0, 0));
        var result = collection.Update(updated);

        Assert.True(result.Succeeded);
        var args = Assert.Single(events);
        Assert.Equal(TaskCollectionChangeKind.Updated, args.Kind);
        Assert.Equal(TaskA, args.TaskId);
        Assert.Same(updated, args.Definition);
    }

    [Fact]
    public void Remove_RaisesRemoved_WithNullDefinition()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(id: TaskA));
        var events = new List<TaskCollectionChangedEventArgs>();
        collection.Changed += (_, args) => events.Add(args);

        var result = collection.Remove(TaskA);

        Assert.True(result.Succeeded);
        var args = Assert.Single(events);
        Assert.Equal(TaskCollectionChangeKind.Removed, args.Kind);
        Assert.Equal(TaskA, args.TaskId);
        Assert.Null(args.Definition);
    }

    [Fact]
    public void SetEnabled_RaisesEnabled_WhenTrue()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(id: TaskA, isEnabled: false));
        var events = new List<TaskCollectionChangedEventArgs>();
        collection.Changed += (_, args) => events.Add(args);

        var result = collection.SetEnabled(TaskA, true);

        Assert.True(result.Succeeded);
        var args = Assert.Single(events);
        Assert.Equal(TaskCollectionChangeKind.Enabled, args.Kind);
        Assert.Equal(TaskA, args.TaskId);
        Assert.NotNull(args.Definition);
        Assert.True(args.Definition.IsEnabled);
    }

    [Fact]
    public void SetEnabled_RaisesDisabled_WhenFalse()
    {
        var collection = new TaskCollection();
        collection.Add(Definition(id: TaskA, isEnabled: true));
        var events = new List<TaskCollectionChangedEventArgs>();
        collection.Changed += (_, args) => events.Add(args);

        var result = collection.SetEnabled(TaskA, false);

        Assert.True(result.Succeeded);
        var args = Assert.Single(events);
        Assert.Equal(TaskCollectionChangeKind.Disabled, args.Kind);
        Assert.Equal(TaskA, args.TaskId);
        Assert.False(args.Definition!.IsEnabled);
    }

    // ---- 失败操作不触发事件 ----

    [Fact]
    public void FailedOperations_DoNotRaise()
    {
        var collection = new TaskCollection();
        var events = new List<TaskCollectionChangedEventArgs>();
        collection.Changed += (_, args) => events.Add(args);

        var task = Definition();
        Assert.True(collection.Add(task).Succeeded); // 首次成功
        events.Clear();

        Assert.False(collection.Add(Definition(id: task.Id)).Succeeded); // DuplicateId
        Assert.False(collection.Update(Definition(id: Guid.NewGuid())).Succeeded); // NotFound
        Assert.False(collection.Remove(Guid.NewGuid()).Succeeded); // NotFound
        Assert.False(collection.SetEnabled(Guid.NewGuid(), true).Succeeded); // NotFound
        Assert.Empty(events); // 所有失败操作均不触发事件
    }

    // ---- TaskService 透传 ----

    [Fact]
    public void TaskService_ForwardsCollectionChanged()
    {
        var service = CreateService();
        var events = new List<TaskCollectionChangedEventArgs>();
        service.CollectionChanged += (_, args) => events.Add(args);

        var task = Definition();
        service.Add(task);

        var args = Assert.Single(events);
        Assert.Equal(TaskCollectionChangeKind.Added, args.Kind);
        Assert.Equal(task.Id, args.TaskId);
    }

    // ---- 辅助 ----

    private static readonly Guid TaskA = new("11111111-1111-1111-1111-111111111111");

    private static TaskService CreateService() => new(
        new NextExecutionCalculator(),
        new TaskInstanceStateMachine(),
        new GuidIdentifierGenerator());

    private static TaskDefinition Definition(
        Guid? id = null,
        TimeOnly? timeOfDay = null,
        bool isEnabled = true) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Kind = timeOfDay is null ? TaskKind.Countdown : TaskKind.DailyAt,
            Action = PowerAction.Shutdown,
            CountdownDuration = timeOfDay is null ? TimeSpan.FromHours(2) : null,
            TargetTimeOfDay = timeOfDay,
            WarningSeconds = 60,
            CreatedAt = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            IsEnabled = isEnabled
        };
}
