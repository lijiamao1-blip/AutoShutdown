using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 CP3 测试：TaskSyncCoordinator 消费本地集合变更事件，防抖合并触发单向同步；
/// 防止同步回环（同步只读本地、只写外部，绝不反向写入本地事实源）；Dispose 解绑。
/// </summary>
public sealed class S22_TaskSyncCoordinatorTests
{
    private const string AppExePath = @"C:\Program Files\AutoShutdown\AutoShutdown.exe";

    private static readonly TimeZoneInfo FixedUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "FixedUtc8",
        TimeSpan.FromHours(8),
        "FixedUtc8",
        "FixedUtc8");

    private static readonly DateTimeOffset Now = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    // ---- 事件触发 → 防抖合并为一次同步 ----

    [Fact]
    public async Task RapidLocalChanges_CoalesceIntoSingleSync()
    {
        var service = CreateTaskService();
        var adapter = new FakeTaskSchedulerAdapter();
        var recording = RecordingWaiter();
        var coordinator = CreateCoordinator(
            service,
            adapter,
            debounceDelay: TimeSpan.FromMilliseconds(100),
            waiter: recording.Waiter);
        var syncCount = 0;
        var completed = new TaskCompletionSource<TaskSyncReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SyncCompleted += (_, report) =>
        {
            syncCount++;
            completed.TrySetResult(report);
        };

        var task1 = DailyTask();
        var task2 = DailyTask();
        service.Add(task1); // 防抖 1 → 等待 waiters[0]
        service.Add(task2); // 取消防抖 1 → 防抖 2 → 等待 waiters[1]
        service.SetEnabled(task1.Id, false); // 取消防抖 2 → 防抖 3 → 等待 waiters[2]

        Assert.Equal(3, recording.Waiters.Count);

        // 只完成最后一次防抖 → 三条变更收敛为一次同步。
        recording.Waiters[2].TrySetResult();
        var report = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, syncCount);
        Assert.True(report.Succeeded);
        // 一次同步收敛全部本地变更：task2 创建；task1 创建（后禁用）。
        Assert.Equal(2, report.Created);
        Assert.Equal(0, report.Failed);
        Assert.Equal(2, adapter.Operations.Count);
        Assert.All(adapter.Operations, op => Assert.Equal("Create", op.Op));
    }

    [Fact]
    public async Task LocalChange_TriggersDebouncedSync_ThatWritesExternalOnly()
    {
        var service = CreateTaskService();
        var adapter = new FakeTaskSchedulerAdapter();
        var coordinator = CreateCoordinator(service, adapter, debounceDelay: TimeSpan.Zero);
        var completed = new TaskCompletionSource<TaskSyncReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SyncCompleted += (_, report) => completed.TrySetResult(report);

        var task = DailyTask();
        service.Add(task);

        var report = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Created);
        var operation = Assert.Single(adapter.Operations);
        Assert.Equal("Create", operation.Op);
        Assert.Equal(TaskSyncNaming.BuildTaskName(task.Id), operation.Name);
        Assert.Equal("--trigger-task " + task.Id.ToString("D"), operation.Spec!.TriggerArgument);
    }

    // ---- 防回环：同步绝不写本地 ----

    [Fact]
    public async Task SyncNow_NeverWritesLocalSourceOfTruth()
    {
        var service = CreateTaskService();
        var adapter = new FakeTaskSchedulerAdapter();
        // 用一个永不完成的等待器，避免 Add 触发的自动同步与本次手动同步并发。
        var coordinator = CreateCoordinator(service, adapter, waiter: (_, _) => NeverCompletingTask());

        var localChanges = 0;
        service.CollectionChanged += (_, _) => localChanges++;

        service.Add(DailyTask());
        var baseline = localChanges; // = 1（Add 自身触发的事件）
        var report = await coordinator.SyncNowAsync(CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Created);
        // 本地事实源未因同步发生任何变更（同步只读 GetAll）：事件计数保持基线不变。
        Assert.Equal(baseline, localChanges);
        // 适配器只见 outbound 变更。
        Assert.NotEmpty(adapter.Operations);
        Assert.All(adapter.Operations, op => Assert.Contains(op.Op, new[] { "Create", "Update", "Delete" }));
        coordinator.Dispose();
    }

    [Fact]
    public async Task ExternalOnlyWrites_DoNotReenterLocalEvents()
    {
        // 外部触发回调（CP4 的 --trigger-task 路径）只回调本地调度/Workflow，绝不调用
        // TaskService 写操作——即使外部任务「执行」也不产生本地集合事件，故无同步回环。
        var service = CreateTaskService();
        var adapter = new FakeTaskSchedulerAdapter();
        var coordinator = CreateCoordinator(service, adapter, debounceDelay: TimeSpan.Zero);
        var completed = new TaskCompletionSource<TaskSyncReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SyncCompleted += (_, report) => completed.TrySetResult(report);

        var localChanges = 0;
        service.CollectionChanged += (_, _) => localChanges++;

        service.Add(DailyTask());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 同步只写外部，未引发新的本地事件（无二次同步、无回环）。
        Assert.Equal(1, localChanges); // 只有测试自身的 Add
        Assert.Single(adapter.Operations);
    }

    // ---- Dispose 解绑 ----

    [Fact]
    public async Task Dispose_Detaches_NoFurtherSyncs()
    {
        var service = CreateTaskService();
        var adapter = new FakeTaskSchedulerAdapter();
        var recording = RecordingWaiter();
        var coordinator = CreateCoordinator(
            service,
            adapter,
            debounceDelay: TimeSpan.FromMilliseconds(50),
            waiter: recording.Waiter);

        coordinator.Dispose();
        service.Add(DailyTask());

        await Task.Delay(150); // 若未解绑，防抖早已启动
        Assert.Empty(recording.Waiters);
        Assert.Empty(adapter.Operations);
    }

    [Fact]
    public async Task SyncNowAfterDispose_ThrowsObjectDisposedException()
    {
        var service = CreateTaskService();
        var adapter = new FakeTaskSchedulerAdapter();
        var coordinator = CreateCoordinator(service, adapter, debounceDelay: TimeSpan.Zero);
        coordinator.Dispose();

        service.Add(DailyTask()); // 已解绑：不触发自动同步
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.SyncNowAsync(CancellationToken.None));
    }

    // ---- 辅助 ----

    private static TaskService CreateTaskService() => new(
        new NextExecutionCalculator(),
        new TaskInstanceStateMachine(),
        new GuidIdentifierGenerator());

    private static TaskSyncService CreateSyncService(FakeTaskSchedulerAdapter adapter)
        => new(new TaskSchedulerMapper(new NextExecutionCalculator()), adapter);

    private static TaskSyncCoordinator CreateCoordinator(
        ITaskService service,
        FakeTaskSchedulerAdapter adapter,
        TimeSpan? debounceDelay = null,
        Func<TimeSpan, CancellationToken, Task>? waiter = null)
        => new(
            service,
            CreateSyncService(adapter),
            new FixedClock(Now, FixedUtc8),
            AppExePath,
            debounceDelay,
            waiter);

    private static (List<TaskCompletionSource> Waiters, Func<TimeSpan, CancellationToken, Task> Waiter) RecordingWaiter()
    {
        var waiters = new List<TaskCompletionSource>();
        Func<TimeSpan, CancellationToken, Task> waiter = (_, cancellationToken) =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add(tcs);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        };
        return (waiters, waiter);
    }

    private static Task NeverCompletingTask()
        => new TaskCompletionSource().Task;

    private static TaskDefinition DailyTask(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = TaskKind.DailyAt,
        Action = PowerAction.Shutdown,
        TargetTimeOfDay = new TimeOnly(9, 0, 0),
        CreatedAt = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
        RealPowerConfirmed = true
    };

    private sealed class FixedClock : IClock
    {
        private readonly DateTimeOffset _utcNow;
        private readonly TimeZoneInfo _timeZone;

        public FixedClock(DateTimeOffset utcNow, TimeZoneInfo timeZone)
        {
            _utcNow = utcNow;
            _timeZone = timeZone;
        }

        public DateTimeOffset UtcNow => _utcNow;
        public TimeZoneInfo LocalTimeZone => _timeZone;
    }
}
