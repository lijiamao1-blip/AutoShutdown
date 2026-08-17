using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 CP2 测试：TaskSyncService 幂等 outbound 同步（创建/更新/删除/查询 + 幂等比较；
/// 部分失败逐任务报告；只删本应用任务；绝不写本地事实源）。用 Fake 适配器，不触碰系统。
/// </summary>
public sealed class S22_TaskSyncServiceTests
{
    private const string AppExePath = @"C:\Program Files\AutoShutdown\AutoShutdown.exe";

    private static readonly TimeZoneInfo FixedUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "FixedUtc8",
        TimeSpan.FromHours(8),
        "FixedUtc8",
        "FixedUtc8");

    // 本地现在：2026-08-17 周一 00:00 UTC（= 08:00 +8）。
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    // ---- 创建 ----

    [Fact]
    public async Task MissingExternalTask_IsCreated_WithMappedSpec()
    {
        var adapter = new FakeTaskSchedulerAdapter();
        var service = CreateService(adapter);
        var task = DailyTask();

        var report = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.Failed);
        Assert.Equal(0, report.Unchanged);
        Assert.Contains(report.Entries, entry =>
            entry.TaskId == task.Id
            && entry.Status == TaskSyncEntryStatus.Created
            && entry.ExternalTaskName == TaskSyncNaming.BuildTaskName(task.Id));

        // 字段映射到达适配器：名字、动作（回调本地应用）、参数（稳定 id）、启用、触发签名。
        var operation = Assert.Single(adapter.Operations);
        Assert.Equal("Create", operation.Op);
        var spec = operation.Spec;
        Assert.NotNull(spec);
        Assert.Equal(TaskSyncNaming.BuildTaskName(task.Id), spec.Name);
        Assert.Equal(AppExePath, spec.AppExePath);
        Assert.Equal("--trigger-task " + task.Id.ToString("D"), spec.TriggerArgument);
        Assert.True(spec.Enabled);
        Assert.Equal(ExternalTriggerKind.Daily, spec.Trigger.Kind);
    }

    [Fact]
    public async Task SyncTwice_SecondRun_IsIdempotent_NoMutations()
    {
        var adapter = new FakeTaskSchedulerAdapter();
        var service = CreateService(adapter);
        var task = DailyTask();

        var first = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);
        var mutationsAfterFirst = adapter.Operations.Count;
        Assert.Equal(1, first.Created);
        Assert.Equal(1, mutationsAfterFirst);

        var second = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.Created);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Deleted);
        Assert.Equal(0, second.Failed);
        Assert.Equal(mutationsAfterFirst, adapter.Operations.Count); // 无任何新变更
    }

    // ---- 更新 ----

    [Fact]
    public async Task ChangedLocalTrigger_TriggersUpdate()
    {
        var task = DailyTask();
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.Seed(ObservedFrom(task, timeOfDay: new TimeOnly(8, 0, 0))); // 外部仍是旧 08:00
        var service = CreateService(adapter);

        var report = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.Equal(1, report.Updated);
        Assert.Equal(0, report.Unchanged);
        Assert.Equal(0, report.Failed);
        var operation = Assert.Single(adapter.Operations);
        Assert.Equal("Update", operation.Op);
    }

    [Fact]
    public async Task DisabledLocalTask_UpdatesExternalToDisabled()
    {
        var task = DailyTask() with { IsEnabled = false };
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.Seed(ObservedFrom(task with { IsEnabled = true })); // 外部仍是启用
        var service = CreateService(adapter);

        var report = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.Equal(1, report.Updated);
        var operation = Assert.Single(adapter.Operations);
        Assert.Equal("Update", operation.Op);
        Assert.False(operation.Spec!.Enabled);
    }

    [Fact]
    public async Task UpdateFailure_WhenExternalTaskMissing_ReportsFailedPerTask()
    {
        var task = DailyTask();
        var adapter = new FakeTaskSchedulerAdapter();
        // 外部有一个名字解析到该 id 但内容失配的任务，但删除/更新都不可达？不：
        // 这里让更新直接返回 NotFound，模拟外部任务在查询后被并发移除。
        adapter.Seed(ObservedFrom(task, timeOfDay: new TimeOnly(8, 0, 0)));
        adapter.FailUpdate(TaskSyncNaming.BuildTaskName(task.Id), ExternalTaskMutationStatus.NotFound);
        var service = CreateService(adapter);

        var report = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Updated);
        var entry = Assert.Single(report.Entries, e => e.TaskId == task.Id);
        Assert.Equal(TaskSyncEntryStatus.Failed, entry.Status);
    }

    // ---- 删除 ----

    [Fact]
    public async Task RemovedLocalTask_DeletesOwnedExternal()
    {
        var staleId = Guid.NewGuid();
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.Seed(ObservedFrom(DailyTask(staleId)));
        var service = CreateService(adapter);

        var report = await service.SyncAsync([], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Deleted);
        var operation = Assert.Single(adapter.Operations);
        Assert.Equal("Delete", operation.Op);
        Assert.Equal(TaskSyncNaming.BuildTaskName(staleId), operation.Name);
    }

    [Fact]
    public async Task LocalTaskNoLongerMapped_DeletesItsExternal()
    {
        // 一次性倒计时已过触发点 → mapper 返回 null → 期望集合为空 → 外部陈旧任务被删。
        var created = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
        var expired = Base(TaskKind.Countdown)
            with { CreatedAt = created, CountdownDuration = TimeSpan.FromMinutes(30) };
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.Seed(ObservedFrom(expired));
        var service = CreateService(adapter);
        var nowAfterFire = created.AddMinutes(60); // 触发点 08:30 已过

        var report = await service.SyncAsync([expired], AppExePath, nowAfterFire, FixedUtc8, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Deleted);
        var operation = Assert.Single(adapter.Operations);
        Assert.Equal("Delete", operation.Op);
    }

    [Fact]
    public async Task ForeignTask_IsNeverDeleted()
    {
        var foreignName = "SomeOtherApp::3FA85F64-5717-4562-B3FC-2C963F66AFA6";
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.Seed(new ExternalTaskState { Name = foreignName, Enabled = true });
        var service = CreateService(adapter);

        var report = await service.SyncAsync([DailyTask()], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.SkippedForeign);
        Assert.Equal(0, report.Deleted);
        Assert.DoesNotContain(adapter.Operations, op => op.Name == foreignName);
    }

    [Fact]
    public async Task DeleteFailure_ReportsFailedPerTask()
    {
        var staleId = Guid.NewGuid();
        var staleName = TaskSyncNaming.BuildTaskName(staleId);
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.Seed(ObservedFrom(DailyTask(staleId)));
        adapter.FailDelete(staleName, ExternalTaskMutationStatus.PermissionDenied);
        var service = CreateService(adapter);

        var report = await service.SyncAsync([], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Deleted);
        var entry = Assert.Single(report.Entries, e => e.TaskId == staleId);
        Assert.Equal(TaskSyncEntryStatus.Failed, entry.Status);
        Assert.Contains("PermissionDenied", entry.Message, StringComparison.Ordinal);
    }

    // ---- 查询 / 权限 ----

    [Fact]
    public async Task PermissionDeniedOnQuery_ReturnsFailedReport_NoMutations()
    {
        var adapter = new FakeTaskSchedulerAdapter
        {
            QueryStatus = ExternalTaskQueryStatus.PermissionDenied,
            QueryMessage = "Access is denied."
        };
        var service = CreateService(adapter);

        var report = await service.SyncAsync([DailyTask()], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(ExternalTaskQueryStatus.PermissionDenied, report.QueryStatus);
        Assert.Equal("Access is denied.", report.QueryFailureMessage);
        Assert.Empty(report.Entries);
        Assert.Empty(adapter.Operations); // 查询失败时不尝试任何变更
    }

    // ---- 部分失败 ----

    [Fact]
    public async Task PartialFailure_ReportsPerTask_CreatedAndFailed()
    {
        var good = DailyTask();
        var bad = DailyTask();
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.FailCreate(TaskSyncNaming.BuildTaskName(bad.Id), ExternalTaskMutationStatus.PermissionDenied);
        var service = CreateService(adapter);

        var report = await service.SyncAsync(
            [good, bad], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Created);
        Assert.Equal(1, report.Failed);
        Assert.Contains(report.Entries, e => e.TaskId == good.Id && e.Status == TaskSyncEntryStatus.Created);
        Assert.Contains(report.Entries, e => e.TaskId == bad.Id && e.Status == TaskSyncEntryStatus.Failed);
        Assert.Contains(adapter.Operations, op => op.Op == "Create" && op.Name == TaskSyncNaming.BuildTaskName(good.Id));
        Assert.Contains(adapter.Operations, op => op.Op == "Create" && op.Name == TaskSyncNaming.BuildTaskName(bad.Id));
    }

    [Fact]
    public async Task AdapterException_IsCapturedAsPerTaskFailure()
    {
        var task = DailyTask();
        var adapter = new FakeTaskSchedulerAdapter();
        adapter.ThrowOnCreate(TaskSyncNaming.BuildTaskName(task.Id));
        var service = CreateService(adapter);

        var report = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Created);
        var entry = Assert.Single(report.Entries, e => e.TaskId == task.Id);
        Assert.Equal(TaskSyncEntryStatus.Failed, entry.Status);
        Assert.Contains("Unexpected adapter failure", entry.Message, StringComparison.Ordinal);
    }

    // ---- 本地事实源不可破坏 ----

    [Fact]
    public async Task LocalSourceOfTruth_IsNeverMutated()
    {
        var task = DailyTask();
        var before = task;
        var adapter = new FakeTaskSchedulerAdapter();
        var service = CreateService(adapter);

        var report = await service.SyncAsync([task], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(before, task); // 输入定义未被改写
        // 适配器只见 outbound 变更；无任何「写回本地」通道（接口边界保证）。
        Assert.All(adapter.Operations, op =>
            Assert.NotEqual("InboundWrite", op.Op));
    }

    // ---- 空输入 ----

    [Fact]
    public async Task EmptyLocalTasks_NoExternal_IsSuccessfulNoOp()
    {
        var adapter = new FakeTaskSchedulerAdapter();
        var service = CreateService(adapter);

        var report = await service.SyncAsync([], AppExePath, Now, FixedUtc8, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.Created);
        Assert.Equal(0, report.Updated);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Entries);
    }

    [Fact]
    public async Task EmptyAppExePath_Throws()
    {
        var adapter = new FakeTaskSchedulerAdapter();
        var service = CreateService(adapter);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SyncAsync([DailyTask()], string.Empty, Now, FixedUtc8, CancellationToken.None));
    }

    // ---- 辅助 ----

    private static TaskSyncService CreateService(FakeTaskSchedulerAdapter adapter)
        => new(new TaskSchedulerMapper(new NextExecutionCalculator()), adapter);

    private static ExternalTaskState ObservedFrom(TaskDefinition definition, TimeOnly? timeOfDay = null)
    {
        var spec = new ExternalTaskSpec
        {
            Name = TaskSyncNaming.BuildTaskName(definition.Id),
            Enabled = definition.IsEnabled,
            Trigger = new ExternalTriggerSpec
            {
                Kind = ExternalTriggerKind.Daily,
                StartTimeOfDay = timeOfDay ?? new TimeOnly(9, 0, 0)
            },
            AppExePath = AppExePath,
            TriggerArgument = TaskSchedulerMapper.BuildTriggerArgument(definition.Id)
        };

        return new ExternalTaskState
        {
            Name = spec.Name,
            Enabled = spec.Enabled,
            TriggerSignature = ExternalTriggerSignature.Build(spec.Trigger),
            ActionPath = spec.AppExePath,
            Arguments = spec.TriggerArgument
        };
    }

    private static TaskDefinition DailyTask(Guid? id = null) => Base(TaskKind.DailyAt)
        with
        {
            Id = id ?? Guid.NewGuid(),
            TargetTimeOfDay = new TimeOnly(9, 0, 0)
        };

    private static TaskDefinition Base(TaskKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Action = PowerAction.Shutdown,
        CreatedAt = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
        RealPowerConfirmed = true
    };
}
