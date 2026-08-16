using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S14 复杂排程规则（Weekdays / NextWorkday / NthWorkdayOfMonth / OneTime）纯函数计算契约测试。
/// 覆盖：过去日期、当日已过点、跨年、月末、第 N 工作日不存在、节假日覆盖、DST gap/overlap、
/// 非系统时区、一次性过期不追溯。所有计算显式传入 now 与 TimeZoneInfo，不读系统时间。
/// </summary>
public sealed class S14_NextExecutionRuleTests
{
    private readonly NextExecutionCalculator _calculator = new();

    private static readonly DayOfWeek[] MonToFri =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
    ];

    private static readonly TimeZoneInfo PacificTime = CreatePacificTimeZone();

    // ---- Weekdays ----

    [Fact]
    public void Weekdays_WhenTodayIsWeekdayAndTimeFuture_ReturnsToday()
    {
        var result = _calculator.Calculate(
            Weekdays(MonToFri, new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void Weekdays_WhenTodayIsWeekend_SkipsToMonday()
    {
        // 2024-01-13 为周六。
        var result = _calculator.Calculate(
            Weekdays(MonToFri, new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 13, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void Weekdays_WhenTargetPassedOnWeekday_ReturnsNextWeekday()
    {
        var result = _calculator.Calculate(
            Weekdays(MonToFri, new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 16, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void Weekdays_WhenHolidayFallsOnWeekday_SkipsToNextWeekday()
    {
        var result = _calculator.Calculate(
            Weekdays(MonToFri, new TimeOnly(15, 0), [new DateOnly(2024, 1, 16)]),
            new DateTimeOffset(2024, 1, 15, 16, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 17, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void Weekdays_WhenYearBoundary_SkipsToNewYear()
    {
        // 2024-12-31 为周二，15:00 已过；下一工作日为 2025-01-01（周三）。
        var result = _calculator.Calculate(
            Weekdays(MonToFri, new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 12, 31, 16, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void Weekdays_WhenWeekdaysMissing_ReturnsMissingWeekdays()
    {
        var result = _calculator.Calculate(
            Weekdays(null, new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingWeekdays, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.ScheduledFireTime);
    }

    [Fact]
    public void Weekdays_WhenWeekdaysEmpty_ReturnsInvalidWeekdays()
    {
        var result = _calculator.Calculate(
            Weekdays([], new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.InvalidWeekdays, result.Status);
    }

    [Fact]
    public void Weekdays_WhenTargetTimeMissing_ReturnsMissingTargetTimeOfDay()
    {
        var result = _calculator.Calculate(
            Weekdays(MonToFri, null),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingTargetTimeOfDay, result.Status);
    }

    [Fact]
    public void Weekdays_WhenDstGap_ForwardsToFirstValidLocalTime()
    {
        // Pacific DST 2024-03-10 02:00 起跳变；02:30 本地时刻不存在 → 前移到 03:00 PDT = 10:00 UTC。
        var result = _calculator.Calculate(
            Weekdays([DayOfWeek.Sunday], new TimeOnly(2, 30)),
            new DateTimeOffset(2024, 3, 9, 20, 0, 0, TimeSpan.Zero),
            PacificTime);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 3, 10, 10, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void Weekdays_WhenAmbiguousTime_ChoosesLaterUtcInstant()
    {
        // Pacific DST 2024-11-03 02:00 结束；01:30 出现两次，较晚 UTC 实例 = 09:30 UTC。
        var result = _calculator.Calculate(
            Weekdays([DayOfWeek.Sunday], new TimeOnly(1, 30)),
            new DateTimeOffset(2024, 11, 2, 20, 0, 0, TimeSpan.Zero),
            PacificTime);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 11, 3, 9, 30, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    // ---- NextWorkday ----

    [Fact]
    public void NextWorkday_WhenTodayWorkdayAndFuture_ReturnsToday()
    {
        var result = _calculator.Calculate(
            NextWorkday(new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void NextWorkday_WhenTargetPassed_ReturnsTomorrow()
    {
        var result = _calculator.Calculate(
            NextWorkday(new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 16, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void NextWorkday_WhenFridayPassed_SkipsWeekendToMonday()
    {
        // 2024-01-19 为周五。
        var result = _calculator.Calculate(
            NextWorkday(new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 19, 16, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 22, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void NextWorkday_WhenHolidayFallsOnNextWorkday_Skips()
    {
        var result = _calculator.Calculate(
            NextWorkday(new TimeOnly(15, 0), [new DateOnly(2024, 1, 16)]),
            new DateTimeOffset(2024, 1, 15, 16, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 17, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void NextWorkday_WhenTargetTimeMissing_ReturnsMissingTargetTimeOfDay()
    {
        var result = _calculator.Calculate(
            NextWorkday(null),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingTargetTimeOfDay, result.Status);
    }

    // ---- NthWorkdayOfMonth ----

    [Fact]
    public void NthWorkdayOfMonth_WhenCurrentMonthPassed_ReturnsNextMonthNthWorkday()
    {
        // 2024-01 第 1 个工作日 = 01-01（周一，已过）；2024-02 第 1 个工作日 = 02-01（周四）。
        var result = _calculator.Calculate(
            NthWorkday(1, new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 9, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void NthWorkdayOfMonth_WhenNthDoesNotExistInMonth_SkipsToLaterMonth()
    {
        // 2024-02（21 个工作日）、03（21）、04（22）均无第 23 个工作日；2024-05 有 23 个，
        // 第 23 个工作日 = 05-31（周五）。
        var result = _calculator.Calculate(
            NthWorkday(23, new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 5, 31, 9, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void NthWorkdayOfMonth_WhenHolidayFallsOnWorkday_ShiftsToNextWorkday()
    {
        // 2024-02-01（周四）为第 1 个工作日；若 02-01 为节假日，则第 1 个工作日变为 02-02（周五）。
        var result = _calculator.Calculate(
            NthWorkday(1, new TimeOnly(9, 0), [new DateOnly(2024, 2, 1)]),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 2, 2, 9, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void NthWorkdayOfMonth_WhenNthWorkdayMissing_ReturnsMissingNthWorkday()
    {
        var result = _calculator.Calculate(
            NthWorkday(null, new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingNthWorkday, result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    public void NthWorkdayOfMonth_WhenNthWorkdayOutOfRange_ReturnsInvalidNthWorkday(int n)
    {
        var result = _calculator.Calculate(
            NthWorkday(n, new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.InvalidNthWorkday, result.Status);
    }

    [Fact]
    public void NthWorkdayOfMonth_WhenTargetTimeMissing_ReturnsMissingTargetTimeOfDay()
    {
        var result = _calculator.Calculate(
            NthWorkday(1, null),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingTargetTimeOfDay, result.Status);
    }

    // ---- OneTime ----

    [Fact]
    public void OneTime_WhenFuture_ReturnsExactDateTime()
    {
        var result = _calculator.Calculate(
            OneTime(new DateTime(2024, 1, 20, 10, 0, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 20, 10, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void OneTime_WhenPast_ReturnsOneTimeExpired()
    {
        var result = _calculator.Calculate(
            OneTime(new DateTime(2024, 1, 10, 10, 0, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.OneTimeExpired, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.ScheduledFireTime);
    }

    [Fact]
    public void OneTime_WhenDateTimeMissing_ReturnsMissingOneTimeDateTime()
    {
        var result = _calculator.Calculate(
            OneTime(null),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingOneTimeDateTime, result.Status);
    }

    [Fact]
    public void OneTime_WhenOnHoliday_StillFires()
    {
        // 一次性日期不因节假日自动改期。
        var result = _calculator.Calculate(
            OneTime(new DateTime(2024, 1, 20, 10, 0, 0), [new DateOnly(2024, 1, 20)]),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 20, 10, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void OneTime_WithNonSystemTimeZone_ConvertsCorrectly()
    {
        var fixedZone = TimeZoneInfo.CreateCustomTimeZone(
            "Test Plus Nine Thirty",
            TimeSpan.FromHours(9.5),
            "Test Plus Nine Thirty",
            "Test Plus Nine Thirty");

        // 本地 2024-01-20 10:00（UTC+9:30）= 2024-01-20 00:30 UTC。
        var result = _calculator.Calculate(
            OneTime(new DateTime(2024, 1, 20, 10, 0, 0)),
            new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
            fixedZone);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 20, 0, 30, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    // ---- Helpers ----

    private static TaskDefinition Weekdays(
        DayOfWeek[]? weekdays,
        TimeOnly? target,
        DateOnly[]? holidays = null) => new()
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.Weekdays,
            Action = PowerAction.Shutdown,
            Weekdays = weekdays,
            TargetTimeOfDay = target,
            HolidayDates = holidays,
            CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

    private static TaskDefinition NextWorkday(
        TimeOnly? target,
        DateOnly[]? holidays = null) => new()
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.NextWorkday,
            Action = PowerAction.Shutdown,
            TargetTimeOfDay = target,
            HolidayDates = holidays,
            CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

    private static TaskDefinition NthWorkday(
        int? n,
        TimeOnly? target,
        DateOnly[]? holidays = null) => new()
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.NthWorkdayOfMonth,
            Action = PowerAction.Shutdown,
            NthWorkday = n,
            TargetTimeOfDay = target,
            HolidayDates = holidays,
            CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

    private static TaskDefinition OneTime(
        DateTime? dateTime,
        DateOnly[]? holidays = null) => new()
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.OneTime,
            Action = PowerAction.Shutdown,
            OneTimeDateTime = dateTime,
            HolidayDates = holidays,
            CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

    private static TimeZoneInfo CreatePacificTimeZone()
    {
        var startTransition = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            3,
            2,
            DayOfWeek.Sunday);
        var endTransition = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            11,
            1,
            DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date.AddDays(-1),
            TimeSpan.FromHours(1),
            startTransition,
            endTransition);

        return TimeZoneInfo.CreateCustomTimeZone(
            "Pacific Test Time",
            TimeSpan.FromHours(-8),
            "Pacific Test Time",
            "Pacific Standard Test Time",
            "Pacific Daylight Test Time",
            [rule]);
    }
}
