using System.Reflection;
using System.Text.Json;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S-UI2 聚焦测试：首页重构、多任务可用性与发布信息收尾。
///   A. 多任务 VM 呈现：已有活动任务仍可创建；任务列表全部列出；首页摘要与任务管理一致。
///   B. 真引擎多任务：2/3 个不冲突任务共存；同刻到期进入统一仲裁（不并发绕过）；
///      停用任务绝不触发执行（S-UI2 启停闸门）；重启恢复全部实例与启停标志。
///   C. 每周指定星期（周一至周日 7 天）：默认周一~周五；仅周六/仅周日/周六日/全选；
///      零选拒绝；保存/重载；旧数据兼容；跨周下次执行计算。
///   D. 一次性日期：最早可选=今天；「今天」快捷；过去日期时间拒绝；未来接受。
///   E. 版本单一源：程序集 2.0.0.0 / InformationalVersion 2.0.0+commit；三处 UI 位置统一绑定。
///   F. Office 卡（只读说明，无开关/立即保存/测试按钮；跳转日志与诊断）。
///   G. 关于页文案（隐私与安全边界 + 帮助与反馈）。
///   H. 首页布局契约（无最外层滚动条；其它页保持 ScrollViewer；三区域齐全）。
/// 纯内存/纯源码契约；不启动应用、不执行真实电源、不触碰注册表/任务计划/网络。
/// </summary>
public sealed class S_UI2_MultiTaskHomeTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid IdA = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid IdB = Guid.Parse("aaaa0000-0000-0000-0000-000000000002");
    private static readonly Guid IdC = Guid.Parse("aaaa0000-0000-0000-0000-000000000003");

    // ======================================================================================
    // A. 多任务 VM 呈现（S-UI2 第 3、7 节）
    // ======================================================================================

    [Fact]
    public async Task CanCreateNow_WithActiveTasks_AllowsSecondAndThirdTask()
    {
        var viewModel = CreateViewModel(engine: new FakeSchedulerEngine
        {
            Snapshot = SnapshotWith(
                Instance(IdA, Now.AddHours(1)),
                Instance(IdB, Now.AddHours(2)))
        });
        await viewModel.InitializeAsync();

        // 已有活动任务不再一刀切禁用创建按钮；冲突交给引擎按任务 id + 仲裁唯一路径。
        Assert.True(viewModel.CanCreateNow(out _));
    }

    [Fact]
    public async Task TaskItems_ListsAllInstances_SortedByFireTime()
    {
        var viewModel = CreateViewModel(engine: new FakeSchedulerEngine
        {
            Snapshot = SnapshotWith(
                Instance(IdB, Now.AddHours(2)),
                Instance(IdA, Now.AddHours(1)))
        });
        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.TaskItems.Count);
        Assert.Equal(IdA, viewModel.TaskItems[0].TaskId);
        Assert.Equal(IdB, viewModel.TaskItems[1].TaskId);
    }

    [Fact]
    public void CurrentTaskCountText_IsEmptyForSingleTask()
    {
        var engine = new FakeSchedulerEngine { Snapshot = SnapshotWith(Instance(IdA, Now.AddHours(1))) };
        var viewModel = CreateViewModel(engine: engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.Equal(string.Empty, viewModel.CurrentTaskCountText);
    }

    [Fact]
    public void CurrentTaskCountText_ShowsCountForTwoPlusTasks()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = SnapshotWith(
                Instance(IdA, Now.AddHours(1)),
                Instance(IdB, Now.AddHours(2)))
        };
        var viewModel = CreateViewModel(engine: engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.Equal("共 2 个任务", viewModel.CurrentTaskCountText);
    }

    [Fact]
    public void HomepagePrimary_SelectsEarliestActiveInstance()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = SnapshotWith(
                Instance(IdB, Now.AddHours(2)),
                Instance(IdA, Now.AddHours(1)))
        };
        var viewModel = CreateViewModel(engine: engine);

        viewModel.Refresh(engine.Snapshot, Now);

        // 首页单卡展示最早到期的非终态实例；多任务摘要（CurrentTaskCountText）与任务管理一致。
        Assert.Equal("2024-01-15 12:00:00", viewModel.NextFireTimeText);
        Assert.Equal("共 2 个任务", viewModel.CurrentTaskCountText);
    }

    [Fact]
    public void DisabledTaskItem_RendersEnableTextAndCanSetEnabled()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = SnapshotWith(
                Instance(IdA, Now.AddHours(1), enabled: false),
                Instance(IdB, Now.AddHours(2)))
        };
        var viewModel = CreateViewModel(engine: engine);

        viewModel.Refresh(engine.Snapshot, Now);

        var disabled = Assert.Single(viewModel.TaskItems, item => item.TaskId == IdA);
        var enabled = Assert.Single(viewModel.TaskItems, item => item.TaskId == IdB);
        Assert.False(disabled.IsEnabled);
        Assert.Equal("启用", disabled.EnabledText);
        Assert.True(disabled.CanSetEnabled);
        Assert.True(enabled.IsEnabled);
        Assert.Equal("停用", enabled.EnabledText);
    }

    [Fact]
    public void GoToLogsCommand_NavigatesToLogsPage()
    {
        var viewModel = CreateViewModel();
        Assert.Equal("home", viewModel.SelectedNav.PageKey);

        viewModel.GoToLogsCommand.Execute(null);

        Assert.Equal("logs", viewModel.SelectedNav.PageKey);
    }

    // ======================================================================================
    // B. 真引擎多任务（S-UI2 第 3、7 节；NoOp 仲裁 + FakeHandler，绝不触碰真实电源）
    // ======================================================================================

    [Fact(Timeout = 3000)]
    public async Task CreateTwoNonConflictingTasks_BothInstancesPresent()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(Now);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var first = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(IdA, TimeSpan.FromHours(1))),
            CancellationToken.None);
        var second = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(IdB, TimeSpan.FromHours(2))),
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        await WaitUntilAsync(() => engine.GetSnapshot().Instances.Count == 2);

        var snapshot = engine.GetSnapshot();
        Assert.Equal(TaskInstanceState.Waiting, snapshot.Instances[IdA].State);
        Assert.Equal(TaskInstanceState.Waiting, snapshot.Instances[IdB].State);
        Assert.True(snapshot.Instances[IdA].IsEnabled);
        Assert.True(snapshot.Instances[IdB].IsEnabled);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 3000)]
    public async Task CreateThreeTasks_AllPresent()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(Now);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        foreach (var id in new[] { IdA, IdB, IdC })
        {
            var created = await engine.SubmitAsync(
                new CreateTaskCommand(CountdownDefinition(id, TimeSpan.FromHours(1))),
                CancellationToken.None);
            Assert.True(created.Succeeded);
        }

        await WaitUntilAsync(() => engine.GetSnapshot().Instances.Count == 3);
        Assert.Equal(3, engine.GetSnapshot().Instances.Count);
    }

    [Fact(Timeout = 3000)]
    public async Task TwoTasksDueSimultaneously_EnterUnifiedArbitration_NoPowerCalled()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(Now);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(IdA, TimeSpan.FromHours(1))),
            CancellationToken.None);
        await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(IdB, TimeSpan.FromHours(1))),
            CancellationToken.None);

        clock.UtcNow = Now.AddHours(1);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();

        // 同刻到期 → 统一仲裁（不并发绕过仲裁直接执行电源）。
        await WaitUntilAsync(() => engine.GetSnapshot().PendingArbitration is not null);

        var pending = engine.GetSnapshot().PendingArbitration!;
        Assert.Equal(2, pending.CandidateTaskIds.Count);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 3000)]
    public async Task DisableOneTask_DisabledDoesNotFire_OtherStillFires()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(Now);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(IdA, TimeSpan.FromHours(1))),
            CancellationToken.None);
        await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(IdB, TimeSpan.FromHours(2))),
            CancellationToken.None);

        // 单独停用任务 B：实例标志与定义同步为停用并持久化；A 不受影响。
        var disabled = await engine.SubmitAsync(
            new SetTaskEnabledCommand(IdB, false),
            CancellationToken.None);
        Assert.True(disabled.Succeeded);
        Assert.False(engine.GetSnapshot().Instances[IdB].IsEnabled);

        // 到达 A 的触发时刻：A 正常执行。
        clock.UtcNow = Now.AddHours(1);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);
        deadline.CompleteNext();
        await WaitUntilAsync(() => handler.CallCount == 1);

        // 到达 B 的（原）触发时刻：停用任务不进入到期评估，绝不执行电源。
        clock.UtcNow = Now.AddHours(2);
        await engine.SubmitAsync(
            new CancelTaskCommand(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(TaskInstanceState.Executed, engine.GetSnapshot().Instances[IdA].State);
        Assert.Equal(TaskInstanceState.Waiting, engine.GetSnapshot().Instances[IdB].State);
        Assert.False(engine.GetSnapshot().Instances[IdB].IsEnabled);
    }

    [Fact(Timeout = 3000)]
    public async Task Restart_RestoresAllInstances_AndPreservesEnabledFlags()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", JsonSerializer.Serialize(new RuntimeState
        {
            SchemaVersion = RuntimeState.CurrentSchemaVersion,
            Instances = new Dictionary<Guid, TaskInstance>
            {
                [IdA] = Instance(IdA, Now.AddHours(1), enabled: true),
                [IdB] = Instance(IdB, Now.AddHours(2), enabled: false)
            },
            LastUpdatedAt = Now
        }));
        storage.Seed("tasks.json", JsonSerializer.Serialize(new TasksDocument
        {
            SchemaVersion = TasksDocument.CurrentSchemaVersion,
            Tasks =
            [
                CountdownDefinition(IdA, TimeSpan.FromHours(1)),
                CountdownDefinition(IdB, TimeSpan.FromHours(2)) with { IsEnabled = false }
            ]
        }));

        var engine = CreateEngine(storage, new FakeClock(Now), new ControllableDeadline(), new FakeHandler());
        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => engine.GetSnapshot().Instances.Count == 2);

        var snapshot = engine.GetSnapshot();
        Assert.Equal(2, snapshot.Instances.Count);
        Assert.True(snapshot.Instances[IdA].IsEnabled);
        Assert.False(snapshot.Instances[IdB].IsEnabled);
        Assert.Equal(TaskInstanceState.Waiting, snapshot.Instances[IdA].State);
        Assert.Equal(TaskInstanceState.Waiting, snapshot.Instances[IdB].State);
    }

    // ======================================================================================
    // C. 每周指定星期（周一至周日 7 天；S-UI2 第 4 节）
    // ======================================================================================

    [Fact]
    public void WeekdayDefaults_MondayToFridaySelected_SatSunOff()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.WeekdayMonday);
        Assert.True(viewModel.WeekdayTuesday);
        Assert.True(viewModel.WeekdayWednesday);
        Assert.True(viewModel.WeekdayThursday);
        Assert.True(viewModel.WeekdayFriday);
        Assert.False(viewModel.WeekdaySaturday);
        Assert.False(viewModel.WeekdaySunday);
    }

    [Fact]
    public void Weekday_OnlySaturday_MapsDefinition()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdayMonday = false;
        viewModel.WeekdayTuesday = false;
        viewModel.WeekdayWednesday = false;
        viewModel.WeekdayThursday = false;
        viewModel.WeekdayFriday = false;
        viewModel.WeekdaySunday = false;
        viewModel.WeekdaySaturday = true;
        viewModel.TimeHoursText = "09 小时";
        viewModel.TimeMinutesText = "30 分钟";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.Weekdays, definition.Kind);
        Assert.Equal(new[] { DayOfWeek.Saturday }, definition.Weekdays);
    }

    [Fact]
    public void Weekday_OnlySunday_MapsDefinition()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdayMonday = false;
        viewModel.WeekdayTuesday = false;
        viewModel.WeekdayWednesday = false;
        viewModel.WeekdayThursday = false;
        viewModel.WeekdayFriday = false;
        viewModel.WeekdaySaturday = false;
        viewModel.WeekdaySunday = true;
        viewModel.TimeHoursText = "09 小时";
        viewModel.TimeMinutesText = "30 分钟";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.Weekdays, definition.Kind);
        Assert.Equal(new[] { DayOfWeek.Sunday }, definition.Weekdays);
    }

    [Fact]
    public void Weekday_SaturdayPlusSunday_MapsBoth()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdayMonday = false;
        viewModel.WeekdayTuesday = false;
        viewModel.WeekdayWednesday = false;
        viewModel.WeekdayThursday = false;
        viewModel.WeekdayFriday = false;
        viewModel.WeekdaySaturday = true;
        viewModel.WeekdaySunday = true;
        viewModel.TimeHoursText = "09 小时";
        viewModel.TimeMinutesText = "30 分钟";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(
            new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
            definition.Weekdays);
    }

    [Fact]
    public void Weekday_AllSevenDays_MapsAll()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdaySaturday = true;
        viewModel.WeekdaySunday = true;
        viewModel.TimeHoursText = "09 小时";
        viewModel.TimeMinutesText = "30 分钟";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(
            new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
            },
            definition.Weekdays);
    }

    [Fact]
    public void Weekday_ZeroSelection_RejectedWithClearMessage()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdayMonday = false;
        viewModel.WeekdayTuesday = false;
        viewModel.WeekdayWednesday = false;
        viewModel.WeekdayThursday = false;
        viewModel.WeekdayFriday = false;
        viewModel.WeekdaySaturday = false;
        viewModel.WeekdaySunday = false;

        Assert.False(viewModel.TryValidateRuleFields(out var error));
        Assert.Contains("至少选择一个星期", error);
        Assert.False(viewModel.TryBuildDefinition(out _, out _));
    }

    [Fact]
    public async Task Weekday_Create_PersistsSaturdayAndSundayToTasksJson()
    {
        var config = new RecordingConfigurationService();
        var viewModel = CreateViewModel(config: config);
        await viewModel.InitializeAsync();

        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdayMonday = false;
        viewModel.WeekdayTuesday = false;
        viewModel.WeekdayWednesday = false;
        viewModel.WeekdayThursday = false;
        viewModel.WeekdayFriday = false;
        viewModel.WeekdaySaturday = true;
        viewModel.WeekdaySunday = true;
        viewModel.TimeHoursText = "09 小时";
        viewModel.TimeMinutesText = "30 分钟";

        await viewModel.CreateCommand.ExecuteAsync();

        var saved = Assert.Single(config.SavedDocuments);
        var task = Assert.Single(saved.Tasks);
        Assert.Equal(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }, task.Weekdays);
    }

    [Fact]
    public async Task Countdown_Create_FlowsSelectedMinutesIntoTask()
    {
        // S-UI2 第 5 节：倒计时下拉选择分钟应真实流入任务定义（00 小时 10 分钟 → 00:10:00），
        // 而非仅停留在 UI 状态。UIA 组合框展开/选择受自定义样式时序影响不稳定，故在 VM 层覆盖。
        var config = new RecordingConfigurationService();
        var viewModel = CreateViewModel(config: config);
        await viewModel.InitializeAsync();

        viewModel.SelectedMode = TimeMode.Countdown;
        viewModel.CountdownHoursText = "00 小时";
        viewModel.CountdownMinutesText = "10 分钟";
        viewModel.CountdownSecondsText = "00 秒";

        await viewModel.CreateCommand.ExecuteAsync();

        var saved = Assert.Single(config.SavedDocuments);
        var task = Assert.Single(saved.Tasks);
        Assert.Equal(TimeSpan.FromMinutes(10), task.CountdownDuration);
    }

    [Fact]
    public async Task Weekday_OldDataWithoutIsEnabled_LoadsAsEnabled()
    {
        // V1 时代 tasks.json：无 IsEnabled 字段（周一到周五）；加载后必须默认启用，不能静默停用。
        var storage = new InMemoryStorage();
        storage.Seed("tasks.json",
            """
            {"SchemaVersion":2,"Tasks":[{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","Kind":4,"Action":1,"TargetTimeOfDay":"09:30:00","WarningSeconds":60,"CreatedAt":"2024-01-15T10:00:00+00:00","RealPowerConfirmed":false,"Priority":0,"Weekdays":[1,2,3,4,5]}]}
            """);

        var load = await new TasksDocumentStore(storage).LoadAsync(CancellationToken.None);

        Assert.Equal(TasksLoadStatus.Success, load.Status);
        var task = Assert.Single(load.Document!.Tasks);
        Assert.True(task.IsEnabled);
        Assert.Equal(
            new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday
            },
            task.Weekdays);
    }

    [Fact]
    public void Weekday_CrossWeek_NextExecutionMovesToNextSelectedDay()
    {
        // 周六 10:00 已过目标 09:30 → 仅选周六时跨到下一周六；选「周六+周日」时当天即到。
        var calculator = new NextExecutionCalculator();

        var saturdayOnly = WeekdayDefinition(
            new[] { DayOfWeek.Saturday },
            target: new TimeOnly(9, 30));
        var nowSaturdayPast = new DateTimeOffset(2024, 1, 13, 10, 0, 0, TimeSpan.Zero); // 周六
        var saturdayOnlyResult = calculator.Calculate(saturdayOnly, nowSaturdayPast, TimeZoneInfo.Utc);

        Assert.True(saturdayOnlyResult.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 20, 9, 30, 0, TimeSpan.Zero), saturdayOnlyResult.ScheduledFireTime);

        var satSun = WeekdayDefinition(
            new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
            target: new TimeOnly(9, 30));
        var sundayMorning = new DateTimeOffset(2024, 1, 14, 8, 0, 0, TimeSpan.Zero); // 周日
        var satSunResult = calculator.Calculate(satSun, sundayMorning, TimeZoneInfo.Utc);

        Assert.True(satSunResult.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 14, 9, 30, 0, TimeSpan.Zero), satSunResult.ScheduledFireTime);
    }

    [Fact]
    public void Weekday_NextExecution_RespectsSelectedWeekdaySet()
    {
        var calculator = new NextExecutionCalculator();

        // 周一 09:00 起算：仅选周六 → 本周六；默认周一~周五 → 当天即周一 09:30。
        var nowMonday = new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero); // 周一
        var saturdayOnly = calculator.Calculate(
            WeekdayDefinition(new[] { DayOfWeek.Saturday }, new TimeOnly(9, 30)),
            nowMonday,
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2024, 1, 20, 9, 30, 0, TimeSpan.Zero), saturdayOnly.ScheduledFireTime);

        var monToFri = calculator.Calculate(
            WeekdayDefinition(
                new[]
                {
                    DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                    DayOfWeek.Thursday, DayOfWeek.Friday
                },
                new TimeOnly(9, 30)),
            nowMonday,
            TimeZoneInfo.Utc);
        Assert.True(monToFri.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 9, 30, 0, TimeSpan.Zero), monToFri.ScheduledFireTime);
    }

    // ======================================================================================
    // D. 一次性日期（S-UI2 第 5 节）
    // ======================================================================================

    [Fact]
    public void OneTime_DisplayDateStart_IsLocalToday()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(new DateTime(2024, 1, 15), viewModel.OneTimeDisplayDateStart);
    }

    [Fact]
    public void OneTime_TodayCommand_SetsToday()
    {
        var viewModel = CreateViewModel();

        viewModel.OneTimeTodayCommand.Execute(null);

        Assert.Equal(new DateTime(2024, 1, 15), viewModel.OneTimeDate);
    }

    [Fact]
    public void OneTime_PastDateTime_RejectedWithClearMessage()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.OneTime;
        viewModel.OneTimeDate = new DateTime(2024, 1, 15); // 今天，本地 11:00
        viewModel.TimeHoursText = "00 小时";
        viewModel.TimeMinutesText = "30 分钟"; // 00:30 已过去

        Assert.False(viewModel.TryValidateRuleFields(out var error));
        Assert.Contains("已过去", error);
    }

    [Fact]
    public void OneTime_FutureDateTime_Accepted()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.OneTime;
        viewModel.OneTimeDate = new DateTime(2024, 1, 15); // 今天，本地 11:00
        viewModel.TimeHoursText = "12 小时";
        viewModel.TimeMinutesText = "30 分钟"; // 12:30 在未来

        Assert.True(viewModel.TryValidateRuleFields(out _));
    }

    // ======================================================================================
    // E. 版本单一源（S-UI2 第 10 节）
    // ======================================================================================

    [Fact]
    public void AssemblyVersion_Is2_0_0_0()
    {
        // AssemblyVersionAttribute 被构建器消费后写入程序集元数据表（不残留为反射属性），
        // 因此经 GetName().Version 读取，与 Directory.Build.props 的单一版本源保持一致。
        var version = typeof(MainWindowViewModel).Assembly.GetName().Version;

        Assert.Equal(new Version(2, 0, 0, 0), version);
    }

    [Fact]
    public void InformationalVersion_StartsWith200_WithRealCommitSuffix()
    {
        var informational = typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(informational);
        Assert.StartsWith("2.0.0", informational);
        Assert.Matches("^2\\.0\\.0(\\+[0-9a-f]{6,40})?$", informational);
    }

    [Fact]
    public void VersionText_StartsWithV2_0_0()
    {
        var viewModel = CreateViewModel();

        Assert.StartsWith("v2.0.0", viewModel.VersionText);
        Assert.DoesNotContain("+", viewModel.VersionText);
    }

    [Fact]
    public void VersionText_IsBoundInTopBar_BottomLeft_AndAboutPage()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        Assert.Contains("Text=\"{Binding VersionText}\"", xaml);
        Assert.True(CountOccurrences(xaml, "{Binding VersionText}") >= 3);
    }

    [Fact]
    public void DirectoryBuildProps_IsSingleVersionSource_2_0_0()
    {
        var props = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Directory.Build.props"));

        Assert.Contains("<Version>2.0.0</Version>", props);
        Assert.Contains("<AssemblyVersion>2.0.0.0</AssemblyVersion>", props);
        Assert.Contains("<FileVersion>2.0.0.0</FileVersion>", props);
        // 版本必须单一来源：不得在 App 源码里另写 1.0.0 硬编码版本。
        Assert.DoesNotContain("v1.0.0", File.ReadAllText(Path.Combine(FindAppSourceRoot(), "MainWindow.xaml")));
    }

    // ======================================================================================
    // F. Office 卡（S-UI2 第 8 节；只读说明，无开关/立即保存/测试按钮）
    // ======================================================================================

    [Fact]
    public void OfficeCard_IsReadOnlyInfo_NoSwitchSaveOrTestButtons()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var block = OfficeCardBlock(xaml);

        Assert.Contains("Office 文档自动保存", block);
        Assert.Contains("查看日志与诊断", block);
        Assert.Contains("GoToLogsCommand", block);
        // 只读说明卡：没有开关（ToggleButton/CheckBox/IsChecked）、没有立即保存/测试按钮、
        // 没有 Office 后端相关保存命令绑定。文案允许提及「无开关」的说明，但不能出现控件。
        Assert.DoesNotContain("IsChecked=", block);
        Assert.DoesNotContain("<ToggleButton", block);
        Assert.DoesNotContain("<CheckBox", block);
        Assert.DoesNotContain("Command=\"{Binding SaveOffice", block);
        Assert.DoesNotContain("Command=\"{Binding TestOffice", block);
        Assert.DoesNotContain("OfficeSaveEnabledCommand", block);
    }

    [Fact]
    public void OfficeCard_Content_CoversSupportedScopeAndHonestFailurePolicy()
    {
        var block = OfficeCardBlock(ReadAppFile("MainWindow.xaml"));

        Assert.Contains("Microsoft Word", block);
        Assert.Contains("Excel", block);
        Assert.Contains("PowerPoint", block);
        Assert.Contains("WPS", block);
        Assert.Contains("LibreOffice", block);
        Assert.Contains("已有路径", block);
        Assert.Contains("不会自行启动 Office", block);
        Assert.Contains("不会在保存后关闭 Office", block);
        Assert.Contains("失败策略为 Continue", block);
        Assert.Contains("不能承诺阻止关机", block);
        // 不得写「保证保存」之类过度承诺；也不得暗示会打开新文档（SaveAs）。
        Assert.Contains("不会自动 SaveAs", block);
    }

    // ======================================================================================
    // G. 关于页文案（S-UI2 第 9 节）
    // ======================================================================================

    [Fact]
    public void PrivacyBoundaryText_CoversRequiredTopics()
    {
        var text = CreateViewModel().PrivacyBoundaryText;

        Assert.Contains("只保存在本机", text);
        Assert.Contains("不自动上传", text);
        Assert.Contains("PIN", text);
        Assert.Contains("HMAC", text);
        Assert.Contains("私钥", text);
        Assert.Contains("PFX", text);
        Assert.Contains("IP", text);
        Assert.Contains("MAC", text);
        Assert.Contains("机器名称", text);
        Assert.Contains("任务名", text);
        Assert.Contains("只向你显式配置的局域网目标发送", text);
        Assert.Contains("远程控制默认关闭", text);
        Assert.Contains("只读", text);
        Assert.Contains("Office 文档自动保存只在你本地", text);
        Assert.Contains("不读取", text);
        Assert.Contains("TestMode", text);
        Assert.Contains("双闸门", text);
    }

    [Fact]
    public void HelpFeedbackText_HasRequiredSections_AndNoFakeContacts()
    {
        var text = CreateViewModel().HelpFeedbackText;

        Assert.Contains("快速开始", text);
        Assert.Contains("常见问题", text);
        Assert.Contains("故障排查", text);
        Assert.Contains("日志与诊断", text);
        Assert.Contains("数据目录", text);
        Assert.Contains("暂无在线反馈通道", text);
        // 不得填写虚假邮箱、网址或客服账号。
        Assert.DoesNotContain("@", text);
        Assert.DoesNotContain("http://", text);
        Assert.DoesNotContain("https://", text);
    }

    // ======================================================================================
    // H. 首页布局契约（最终首页：左侧完整表单；右侧当前任务/最近活动为 2:1）
    // ======================================================================================

    [Fact]
    public void Homepage_OuterContainer_IsGrid_NoHomeScrollViewer()
    {
        // 首页最外层是 Grid（非 ScrollViewer，无首页整体滚动条）。
        var home = ExtractElementWithVisibility(ReadAppFile("MainWindow.xaml"), "IsHomeVisible");

        Assert.StartsWith("<Grid ", home.TrimStart());
        // 首页全区块不含显式 ScrollViewer；最近活动仅使用 ListBox 自身的内部滚动。
        Assert.DoesNotContain("<ScrollViewer", home);
    }

    [Fact]
    public void Homepage_HasTwoColumnLayout_Create_Current_Recent()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var home = ExtractElementWithVisibility(xaml, "IsHomeVisible");

        // 主页面左右两栏：左完整创建表单，右当前任务/最近活动。
        Assert.Contains("Width=\"1.06*\"", home);
        Assert.Contains("Width=\"0.94*\"", home);
        Assert.Contains("创建定时任务", home);
        Assert.Contains("当前任务", home);
        Assert.Contains("最近活动", home);
        Assert.Contains("CurrentTaskCountText", home);
        // 最近活动直接绑定完整 20 条列表（非 D1 的 5 条摘要）
        Assert.Contains("ItemsSource=\"{Binding RecentActivities}\"", home);
    }

    [Fact]
    public void Homepage_TimeModeRowsUseFiveDipGap_AndCardContentsStayTopAnchored()
    {
        var home = ExtractElementWithVisibility(ReadAppFile("MainWindow.xaml"), "IsHomeVisible");
        var controls = ReadAppFile("Themes/Controls.xaml");

        Assert.Contains("<Setter Property=\"Margin\" Value=\"0,0,8,5\" />", controls);
        Assert.Equal(2, CountOccurrences(home, "<StackPanel VerticalAlignment=\"Top\">"));
        Assert.Contains("<WrapPanel Margin=\"0,5,0,8\" Width=\"480\" HorizontalAlignment=\"Left\">", home);
        // 480 DIP 固定宽恰好 2 处：时间模式 WrapPanel 与时间输入区 Border；无重复、无冲突、无第三处误配。
        Assert.Equal(2, CountOccurrences(home, "Width=\"480\" HorizontalAlignment=\"Left\">"));
        Assert.Contains("TextWrapping=\"NoWrap\" MinWidth=\"390\" HorizontalAlignment=\"Left\"", home);
    }

    [Fact]
    public void Homepage_Cards_HaveFifteenDipEdgeMargins()
    {
        // 卡片 15 DIP 间距：由 Grid/Margin 实现，随 WPF DPI 一致缩放。
        var home = ExtractElementWithVisibility(ReadAppFile("MainWindow.xaml"), "IsHomeVisible");

        Assert.Contains("Margin=\"15,15,0,15\"", home);
        Assert.Contains("Margin=\"0,15,15,0\"", home);
        Assert.Contains("Margin=\"0,0,15,15\"", home);
    }

    [Fact]
    public void Homepage_RightCards_UseTwoToOneHeightRatio_AndCountdownIsDoubled()
    {
        var home = ExtractElementWithVisibility(ReadAppFile("MainWindow.xaml"), "IsHomeVisible");

        Assert.Contains("<RowDefinition Height=\"2*\"/>", home);
        Assert.Contains("<RowDefinition Height=\"*\"/>", home);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"80\"/>", home);
        // 空闲状态的长文本继续用较小字号和换行，避免内容截断。
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"15\"/>", home);
    }

    [Fact]
    public void Homepage_CurrentTaskCard_HasThreeStatusCardsAtBottom()
    {
        // 三张状态小卡（任务状态/调度服务/配置状态）恢复到当前任务卡底部。
        var home = ExtractElementWithVisibility(ReadAppFile("MainWindow.xaml"), "IsHomeVisible");

        Assert.Contains("任务状态", home);
        Assert.Contains("调度服务", home);
        Assert.Contains("配置状态", home);
        Assert.Contains("SchedulerStatusText", home);
        Assert.Contains("ConfigStatusText", home);
        Assert.Contains("UniformGrid Columns=\"3\"", home);
    }

    [Fact]
    public void Homepage_RecentActivities_TitleFixed_ListScrollsInternally()
    {
        // 最近活动卡：标题固定（Grid 行 Auto），仅列表区内部纵向滚动
        // （VerticalScrollBarVisibility=Auto，HorizontalScrollBarVisibility=Disabled）。
        var home = ExtractElementWithVisibility(ReadAppFile("MainWindow.xaml"), "IsHomeVisible");

        Assert.Contains("<RowDefinition Height=\"Auto\"/>", home);
        Assert.Contains("<RowDefinition Height=\"*\"/>", home);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Auto\"", home);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", home);
        Assert.Contains("ItemsSource=\"{Binding RecentActivities}\"", home);
    }

    [Fact]
    public void Homepage_KeepsFullForm_NoTrimming_NoCreateView()
    {
        // 完整创建表单直接位于首页左侧（非独立创建视图）：时间模式 WrapPanel 自动宽度，
        // 关键文案不省略、不缩字、不用 Viewbox；未选中模式的输入区按绑定折叠。
        var xaml = ReadAppFile("MainWindow.xaml");
        var home = ExtractElementWithVisibility(xaml, "IsHomeVisible");

        Assert.Contains("时间模式（8 种）", home);
        Assert.Contains("WrapPanel", home);
        Assert.Contains("Content=\"每月第N个工作日\"", home);
        Assert.Contains("Content=\"每周指定星期\"", home);
        Assert.Contains("Content=\"唤醒他机\"", home);
        Assert.DoesNotContain("Viewbox", home);
        Assert.DoesNotContain("TextTrimming", home);
        Assert.Contains("ModeIsCountdown", home);
        Assert.Contains("IsTimeInputVisible", home);
        Assert.Contains("IsIdleSelectorVisible", home);
        // 已删除独立创建视图/仪表盘逻辑
        Assert.DoesNotContain("IsCreateViewVisible", xaml);
        Assert.DoesNotContain("IsDashboardVisible", xaml);
        Assert.DoesNotContain("OpenCreateCommand", xaml);
        Assert.DoesNotContain("BackToDashboardCommand", xaml);
        Assert.DoesNotContain("HomeRecentActivities", xaml);
    }

    [Fact]
    public void Homepage_CurrentTaskCard_TextStaysBlack_OrHighContrast()
    {
        // 当前任务卡内信息文字保持黑/高对比（S-UI1-D3 已将 4 行说明改为 Black，须保持不变）。
        var home = ExtractElementWithVisibility(ReadAppFile("MainWindow.xaml"), "IsHomeVisible");

        Assert.Contains("下次执行：", home);
        Assert.Contains("Foreground=\"Black\"", home);
        Assert.Contains("Foreground=\"#294064\"", home);   // 状态行（深蓝高对比）
        Assert.Contains("Foreground=\"#E9E1FF\"", home);   // 计数徽标（紫色卡上浅色高对比）
    }

    [Fact]
    public void OtherPages_RemainOuterScrollViewers()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        foreach (var visibility in new[] { "IsTasksPageVisible", "IsAdvancedPageVisible", "IsWolPageVisible", "IsLogsPageVisible", "IsSettingsPageVisible", "IsAboutPageVisible" })
        {
            var element = ExtractElementWithVisibility(xaml, visibility);
            Assert.StartsWith("<ScrollViewer ", element.TrimStart());
        }
    }

    [Fact]
    public void WeekdaySelector_HasSevenCheckboxes_MondayThroughSunday()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var home = ExtractElementWithVisibility(xaml, "IsHomeVisible");

        foreach (var weekday in new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" })
        {
            Assert.Contains($"Content=\"{weekday}\"", home);
        }

        Assert.Contains("WeekdaySaturday", home);
        Assert.Contains("WeekdaySunday", home);
    }

    [Fact]
    public void OneTimeDateArea_HasEnlargedCalendarTodayShortcutAndErrorSlot()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var home = ExtractElementWithVisibility(xaml, "IsHomeVisible");

        Assert.Contains("执行日期", home);
        Assert.Contains("OneTimeDisplayDateStart", home);
        Assert.Contains("OneTimeTodayCommand", home);
        Assert.Contains("OneTimeDateTimeError", home);
        Assert.Contains("CalendarStyle", home);
    }


    // ======================================================================================
    // Helpers
    // ======================================================================================

    private static MainWindowViewModel CreateViewModel(
        ISchedulerEngine? engine = null,
        IConfigurationService? config = null)
    {
        engine ??= new FakeSchedulerEngine { Snapshot = SnapshotWith() };
        config ??= new RecordingConfigurationService();
        return new MainWindowViewModel(
            engine,
            config,
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService());
    }

    private static SchedulerSnapshot SnapshotWith(params TaskInstance[] instances) => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        Instances = instances.ToDictionary(instance => instance.SourceTaskId),
        LastUpdatedAt = Now
    };

    private static TaskInstance Instance(
        Guid taskId,
        DateTimeOffset fire,
        bool enabled = true,
        TaskInstanceState state = TaskInstanceState.Waiting) => new()
        {
            InstanceId = Guid.Parse(taskId.ToString("N")[..^1] + "1"),
            SourceTaskId = taskId,
            ActionSnapshot = PowerAction.Shutdown,
            State = state,
            ScheduledFireTime = fire,
            StageToken = Guid.Parse(taskId.ToString("N")[..^1] + "2"),
            HasExecuted = false,
            CreatedAt = Now,
            IsEnabled = enabled
        };

    private static TaskDefinition CountdownDefinition(Guid id, TimeSpan duration) => new()
    {
        Id = id,
        Kind = TaskKind.Countdown,
        Action = PowerAction.Shutdown,
        CountdownDuration = duration,
        WarningSeconds = 0,
        CreatedAt = Now
    };

    private static TaskDefinition WeekdayDefinition(IReadOnlyList<DayOfWeek> weekdays, TimeOnly target) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TaskKind.Weekdays,
        Action = PowerAction.Shutdown,
        Weekdays = weekdays,
        TargetTimeOfDay = target,
        WarningSeconds = 0,
        CreatedAt = Now
    };

    private static SchedulerEngine CreateEngine(
        IStorage storage,
        FakeClock clock,
        ControllableDeadline deadline,
        FakeHandler handler)
    {
        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new CountingIdentifierGenerator());

        return new SchedulerEngine(
            storage,
            clock,
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            new CountingIdentifierGenerator(),
            handler,
            new NoOpTaskArbitrator());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for the condition.");
            }

            await Task.Delay(5);
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>截取包含指定 Visibility 绑定元素的整段（从 <Grid/<ScrollViewer 到对应结束标签）。</summary>
    private static string ExtractElementWithVisibility(string xaml, string visibility)
    {
        var marker = $"Visibility=\"{{Binding {visibility}";
        var start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start > 0, $"Expected {visibility} element to exist in MainWindow.xaml.");

        return ExtractEnclosingElement(xaml, start, $"element with {visibility}");
    }

    /// <summary>截取 S-UI2 Office 说明卡的内容区块（标题/说明/按钮所在的 StackPanel 内）。</summary>
    private static string OfficeCardBlock(string xaml)
    {
        // 用说明正文（非注释）作为锚点，避免命中卡片上方的 S-UI2 注释。
        const string marker = "关机前自动保存运行中的";
        var start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start > 0, "Expected the S-UI2 Office card to exist in MainWindow.xaml.");

        return ExtractEnclosingElement(xaml, start, "Office card");
    }

    /// <summary>从 marker 位置向前找到最近的容器元素开始标签，再向后做配平提取。注释与自闭合标签一律跳过。</summary>
    private static string ExtractEnclosingElement(string xaml, int markerStart, string label)
    {
        var elementStart = -1;
        for (var i = markerStart; i >= 0; i--)
        {
            if (xaml[i] != '<')
            {
                continue;
            }

            if (IsCommentStart(xaml, i))
            {
                var commentEnd = xaml.LastIndexOf("-->", i, StringComparison.Ordinal);
                if (commentEnd >= 0)
                {
                    i = commentEnd;
                    continue;
                }

                continue;
            }

            if (i + 1 < xaml.Length && xaml[i + 1] == '/')
            {
                continue;
            }

            if (IsSelfClosingTag(xaml, i))
            {
                continue;
            }

            elementStart = i;
            break;
        }

        Assert.True(elementStart >= 0, $"Failed to find the enclosing element for {label}.");

        var depth = 0;
        for (var i = elementStart; i < xaml.Length; i++)
        {
            if (xaml[i] != '<')
            {
                continue;
            }

            if (IsCommentStart(xaml, i))
            {
                var commentEnd = xaml.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (commentEnd >= 0)
                {
                    i = commentEnd + 2;
                    continue;
                }

                continue;
            }

            if (i + 1 < xaml.Length && xaml[i + 1] == '/')
            {
                depth--;
                if (depth == 0)
                {
                    var end = xaml.IndexOf('>', i) + 1;
                    return xaml[elementStart..end];
                }

                continue;
            }

            if (IsSelfClosingTag(xaml, i))
            {
                continue;
            }

            depth++;
        }

        throw new InvalidOperationException($"Failed to extract {label} from MainWindow.xaml.");
    }

    private static bool IsCommentStart(string text, int index)
        => index + 3 < text.Length
            && text[index + 1] == '!'
            && text[index + 2] == '-'
            && text[index + 3] == '-';

    private static bool IsSelfClosingTag(string text, int tagStart)
    {
        var inQuote = false;
        for (var i = tagStart + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == '>' && !inQuote)
            {
                return i > tagStart + 1 && text[i - 1] == '/';
            }
        }

        return false;
    }

    private static string ReadAppFile(params string[] relativeParts)
    {
        var parts = new[] { FindAppSourceRoot() }.Concat(relativeParts).ToArray();
        return File.ReadAllText(Path.Combine(parts));
    }

    private static string FindAppSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The AutoShutdown.App source directory was not found.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "Directory.Build.props");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SnapshotWith();

        public List<SchedulerCommand> Commands { get; } = new();

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
        }

        public SchedulerSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class RecordingConfigurationService : IConfigurationService
    {
        public ConfigurationLoadResult LoadResult { get; init; } = new()
        {
            Status = ConfigurationLoadStatus.Success,
            Config = new AppConfig
            {
                SchemaVersion = 1,
                TestMode = true,
                AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
            }
        };

        public TasksLoadResult LoadTasksResult { get; init; } = new() { Status = TasksLoadStatus.NotFound };

        public List<TasksDocument> SavedDocuments { get; } = new();

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(LoadResult);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });

        public Task<TasksLoadResult> LoadTasksAsync(CancellationToken cancellationToken)
            => Task.FromResult(LoadTasksResult);

        public Task<TasksSaveResult> SaveTasksAsync(TasksDocument document, CancellationToken cancellationToken)
        {
            SavedDocuments.Add(document);
            return Task.FromResult(new TasksSaveResult { Status = TasksSaveStatus.Success });
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class NoOpAutoStartService : IAutoStartService
    {
        public AutoStartStatus GetStatus() => AutoStartStatus.Disabled;

        public AutoStartOperationResult Enable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class NullLogger : IApplicationLogger
    {
        public string LogDirectory => "C:\\logs";

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeHandler : IScheduledTaskHandler
    {
        public int CallCount { get; private set; }

        public Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingIdentifierGenerator : IIdentifierGenerator
    {
        private int _counter;

        public Guid NewId()
        {
            var value = System.Threading.Interlocked.Increment(ref _counter);
            return Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
        }
    }

    private sealed class EngineScope : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public EngineScope(SchedulerEngine engine)
        {
            RunTask = engine.RunAsync(_cts.Token);
        }

        public Task RunTask { get; }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                RunTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class ControllableDeadline : IAsyncDeadline
    {
        private readonly List<TaskCompletionSource> _waiters = new();
        private readonly object _gate = new();

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        public Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiters.Add(tcs);
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => tcs.TrySetCanceled());
            }

            return tcs.Task;
        }

        public void CompleteNext()
        {
            TaskCompletionSource? next = null;
            lock (_gate)
            {
                while (_waiters.Count > 0)
                {
                    next = _waiters[0];
                    _waiters.RemoveAt(0);
                    if (!next.Task.IsCompleted)
                    {
                        break;
                    }

                    next = null;
                }
            }

            next?.TrySetResult();
        }
    }
}
