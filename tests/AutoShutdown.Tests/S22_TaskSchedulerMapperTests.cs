using AutoShutdown.Core.Idle;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S22 CP1 测试：TaskSchedulerMapper 冻结映射（稳定命名、字段映射、触发形态、禁用、时区）。
/// 纯映射，不触碰任何系统。
/// </summary>
public sealed class S22_TaskSchedulerMapperTests
{
    private const string AppExePath = @"C:\Program Files\AutoShutdown\AutoShutdown.exe";

    private static readonly TimeZoneInfo FixedUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "FixedUtc8",
        TimeSpan.FromHours(8),
        "FixedUtc8",
        "FixedUtc8");

    private readonly TaskSchedulerMapper _mapper = new(new NextExecutionCalculator());

    // ---- 稳定命名（专属目录 + 应用标识 + 稳定 id 三重条件中的名称部分） ----

    [Fact]
    public void BuildTaskName_UsesAppIdentifierAndStableTaskId()
    {
        var id = Guid.NewGuid();
        var name = TaskSyncNaming.BuildTaskName(id);

        Assert.StartsWith(TaskSyncNaming.AppIdentifier + "--", name);
        Assert.Contains(id.ToString("D"), name);
        Assert.DoesNotContain(name, character => "\\/:*?\"<>|".Contains(character));

        Assert.True(TaskSyncNaming.TryParseOwnedTaskName(name, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomeOtherApp::task")]
    [InlineData("AutoShutdownV2")]
    [InlineData("AutoShutdownV2::3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("AutoShutdownV2--not-a-guid")]
    [InlineData("AutoShutdownV2--00000000-0000-0000-0000-000000000000")]
    [InlineData("AutoShutdownV2--3FA85F64-5717-4562-B3FC-2C963F66AFA6-extra")]
    public void TryParseOwnedTaskName_RejectsForeignOrMalformedNames(string? name)
    {
        Assert.False(TaskSyncNaming.TryParseOwnedTaskName(name, out _));
    }

    [Fact]
    public void BuildTaskName_RejectsEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => TaskSyncNaming.BuildTaskName(Guid.Empty));
    }

    // ---- 触发形态映射 ----

    [Fact]
    public void Countdown_MapsToOneTimeTriggerAtCreatedAtPlusDuration()
    {
        var created = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
        var definition = Base(TaskKind.Countdown)
            with { CreatedAt = created, CountdownDuration = TimeSpan.FromMinutes(30) };
        var now = created.AddMinutes(-10);

        var spec = _mapper.Map(definition, AppExePath, now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.OneTime, spec.Trigger.Kind);
        // 08:00 UTC + 30min = 08:30 UTC = 16:30 本地(+8)
        Assert.Equal(new DateTime(2026, 8, 17, 16, 30, 0), spec.Trigger.StartBoundary);
    }

    [Fact]
    public void Countdown_AlreadyPassed_ReturnsNull()
    {
        var created = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
        var definition = Base(TaskKind.Countdown)
            with { CreatedAt = created, CountdownDuration = TimeSpan.FromMinutes(30) };
        var now = created.AddMinutes(60);

        Assert.Null(_mapper.Map(definition, AppExePath, now, FixedUtc8));
    }

    [Fact]
    public void TodayAt_FutureToday_MapsToOneTimeTodayAtTarget()
    {
        var definition = Base(TaskKind.TodayAt)
            with { TargetTimeOfDay = new TimeOnly(18, 30, 0) };
        // 本地时间现在 = 17:00(+8) → UTC 09:00
        var now = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

        var spec = _mapper.Map(definition, AppExePath, now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.OneTime, spec.Trigger.Kind);
        Assert.Equal(new DateTime(2026, 8, 17, 18, 30, 0), spec.Trigger.StartBoundary);
    }

    [Fact]
    public void TodayAt_AlreadyPassed_ReturnsNull()
    {
        var definition = Base(TaskKind.TodayAt)
            with { TargetTimeOfDay = new TimeOnly(7, 0, 0) };
        // 本地现在 = 18:00 → 今天 7:00 已过
        var now = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

        Assert.Null(_mapper.Map(definition, AppExePath, now, FixedUtc8));
    }

    [Fact]
    public void DailyAt_MapsToDailyTriggerWithLocalTime()
    {
        var definition = Base(TaskKind.DailyAt)
            with { TargetTimeOfDay = new TimeOnly(23, 45, 0) };

        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.Daily, spec.Trigger.Kind);
        Assert.Equal(new TimeOnly(23, 45, 0), spec.Trigger.StartTimeOfDay);
        Assert.Null(spec.Trigger.StartBoundary);
    }

    [Fact]
    public void Weekdays_MapsToWeeklyTriggerWithSortedDistinctDays()
    {
        var definition = Base(TaskKind.Weekdays)
            with
            {
                TargetTimeOfDay = new TimeOnly(9, 0, 0),
                Weekdays = [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday]
            };

        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.Weekly, spec.Trigger.Kind);
        Assert.Equal(new TimeOnly(9, 0, 0), spec.Trigger.StartTimeOfDay);
        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
            spec.Trigger.DaysOfWeek);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(11, 3)]
    [InlineData(16, 4)]
    [InlineData(20, 4)]
    [InlineData(21, 5)]
    [InlineData(23, 5)]
    public void NthWorkdayOfMonth_MapsToMonthlyOnWeekdaysWithWeek(int nthWorkday, int expectedWeek)
    {
        var definition = Base(TaskKind.NthWorkdayOfMonth)
            with
            {
                TargetTimeOfDay = new TimeOnly(8, 0, 0),
                NthWorkday = nthWorkday
            };

        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.MonthlyOnWeekdays, spec.Trigger.Kind);
        Assert.Equal(expectedWeek, spec.Trigger.NthWeek);
        Assert.Equal(new TimeOnly(8, 0, 0), spec.Trigger.StartTimeOfDay);
        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            spec.Trigger.DaysOfWeek);
    }

    [Fact]
    public void NextWorkday_MapsToOneTimeAtNextWorkdayTarget()
    {
        // 2026-08-17 是周一；next workday 目标 17:30 → 当天 17:30
        var definition = Base(TaskKind.NextWorkday)
            with { TargetTimeOfDay = new TimeOnly(17, 30, 0) };
        // 本地 = 周一 08:00 (+8) → UTC 00:00
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        var spec = _mapper.Map(definition, AppExePath, now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.OneTime, spec.Trigger.Kind);
        Assert.Equal(new DateTime(2026, 8, 17, 17, 30, 0), spec.Trigger.StartBoundary);
    }

    [Fact]
    public void OneTime_Future_MapsToOneTimeAtPreciseLocalTime()
    {
        var definition = Base(TaskKind.OneTime)
            with { OneTimeDateTime = new DateTime(2026, 8, 20, 12, 0, 0) };
        // OneTimeDateTime 是本地墙钟 → UTC 04:00；本地现在 = 08-17
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        var spec = _mapper.Map(definition, AppExePath, now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.OneTime, spec.Trigger.Kind);
        Assert.Equal(new DateTime(2026, 8, 20, 12, 0, 0), spec.Trigger.StartBoundary);
    }

    [Fact]
    public void OneTime_Expired_ReturnsNull()
    {
        var definition = Base(TaskKind.OneTime)
            with { OneTimeDateTime = new DateTime(2026, 8, 10, 12, 0, 0) };
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(_mapper.Map(definition, AppExePath, now, FixedUtc8));
    }

    [Fact]
    public void Idle_MapsToIdleTriggerWithResolvedThreshold()
    {
        var definition = Base(TaskKind.Idle)
            with { IdleThresholdSeconds = 1200 };

        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(ExternalTriggerKind.Idle, spec.Trigger.Kind);
        Assert.Equal(TimeSpan.FromMinutes(20), spec.Trigger.IdleDuration);
    }

    [Fact]
    public void Idle_WithoutThreshold_InheritsGlobalDefault()
    {
        var definition = Base(TaskKind.Idle);

        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(IdleShutdownRule.GlobalDefaultThreshold, spec.Trigger.IdleDuration);
    }

    // ---- 禁用 / 动作 / 描述 / 参数 ----

    [Fact]
    public void DisabledTask_MapsToDisabledExternalTask()
    {
        var definition = Daily() with { IsEnabled = false };

        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.False(spec.Enabled);
    }

    [Fact]
    public void ExternalAction_AlwaysCallsBackLocalApp_WithStableTaskId()
    {
        var definition = Daily();
        var id = definition.Id;

        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Equal(AppExePath, spec.AppExePath);
        Assert.Equal("--trigger-task " + id.ToString("D"), spec.TriggerArgument);
        // 动作绝不携带任何电源命令文本
        Assert.DoesNotContain("shutdown", spec.TriggerArgument, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("power", spec.TriggerArgument, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Description_ContainsAppIdTaskIdAndAction()
    {
        var definition = Daily();
        var spec = _mapper.Map(definition, AppExePath, Now, FixedUtc8);

        Assert.NotNull(spec);
        Assert.Contains(TaskSyncNaming.AppIdentifier, spec.Description);
        Assert.Contains(definition.Id.ToString("D"), spec.Description);
        Assert.Contains(definition.Action.ToString(), spec.Description);
    }

    [Fact]
    public void EmptyAppExePath_Throws()
    {
        var definition = Base(TaskKind.DailyAt);

        Assert.Throws<ArgumentException>(() => _mapper.Map(definition, string.Empty, Now, FixedUtc8));
    }

    [Fact]
    public void StructurallyInvalidDefinition_ReturnsNull()
    {
        var definition = Base(TaskKind.Countdown) with { CountdownDuration = null };

        Assert.Null(_mapper.Map(definition, AppExePath, Now, FixedUtc8));
    }

    private static DateTimeOffset Now => new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    private static TaskDefinition Daily()
        => Base(TaskKind.DailyAt) with { TargetTimeOfDay = new TimeOnly(9, 0, 0) };

    private static TaskDefinition Base(TaskKind kind)
        => new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Action = PowerAction.Shutdown,
            CreatedAt = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            RealPowerConfirmed = true
        };
}
