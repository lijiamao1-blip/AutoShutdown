using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class NextExecutionCalculatorTests
{
    private readonly NextExecutionCalculator _calculator = new();

    private static readonly TimeZoneInfo PacificTime = CreatePacificTimeZone();

    [Fact]
    public void Countdown_WhenValid_ReturnsCreatedAtPlusDuration()
    {
        var definition = Countdown(
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(2));

        var result = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(NextExecutionStatus.Success, result.Status);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public void Countdown_WhenCalculatedWithDifferentNowValues_TargetDoesNotDrift()
    {
        var definition = Countdown(
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(2));

        var first = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        var second = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 11, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(first, second);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero), first.ScheduledFireTime);
    }

    [Fact]
    public void Countdown_WhenDurationIsMissing_ReturnsMissingCountdownDuration()
    {
        var definition = Countdown(
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            null);

        var result = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingCountdownDuration, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.ScheduledFireTime);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public void Countdown_WhenDurationIsZeroOrNegative_ReturnsInvalidCountdownDuration()
    {
        var createdAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

        var zero = _calculator.Calculate(
            Countdown(createdAt, TimeSpan.Zero),
            now,
            TimeZoneInfo.Utc);
        var negative = _calculator.Calculate(
            Countdown(createdAt, TimeSpan.FromHours(-1)),
            now,
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.InvalidCountdownDuration, zero.Status);
        Assert.Equal(NextExecutionStatus.InvalidCountdownDuration, negative.Status);
    }

    [Fact]
    public void Countdown_WhenTargetIsBeforeOrEqualToNow_ReturnsNoFutureOccurrence()
    {
        var now = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var before = _calculator.Calculate(
            Countdown(new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1)),
            now,
            TimeZoneInfo.Utc);
        var equal = _calculator.Calculate(
            Countdown(new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(2)),
            now,
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.NoFutureOccurrence, before.Status);
        Assert.Equal(NextExecutionStatus.NoFutureOccurrence, equal.Status);
    }

    [Fact]
    public void TodayAt_WhenTodayTargetIsInTheFuture_ReturnsSuccess()
    {
        var definition = TodayAt(new TimeOnly(15, 0));

        var result = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void TodayAt_WhenTodayTargetIsPassedOrEqualToNow_ReturnsNoFutureOccurrence()
    {
        var now = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);

        var passed = _calculator.Calculate(
            TodayAt(new TimeOnly(9, 0)),
            now,
            TimeZoneInfo.Utc);
        var equal = _calculator.Calculate(
            TodayAt(new TimeOnly(10, 0)),
            now,
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.NoFutureOccurrence, passed.Status);
        Assert.Equal(NextExecutionStatus.NoFutureOccurrence, equal.Status);
    }

    [Fact]
    public void TodayAt_WhenTargetTimeOfDayIsMissing_ReturnsMissingTargetTimeOfDay()
    {
        var result = _calculator.Calculate(
            TodayAt(null),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.MissingTargetTimeOfDay, result.Status);
        Assert.Null(result.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_WhenTodayTargetIsInTheFuture_ReturnsToday()
    {
        var result = _calculator.Calculate(
            DailyAt(new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_WhenTodayTargetIsPassed_ReturnsTomorrow()
    {
        var result = _calculator.Calculate(
            DailyAt(new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 16, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_WhenTodayTargetEqualsNow_ReturnsTomorrow()
    {
        var result = _calculator.Calculate(
            DailyAt(new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 15, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 15, 0, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_AcrossMidnightMonthAndYear_ReturnsCorrectDates()
    {
        var midnight = _calculator.Calculate(
            DailyAt(new TimeOnly(0, 30)),
            new DateTimeOffset(2024, 1, 15, 23, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2024, 1, 16, 0, 30, 0, TimeSpan.Zero), midnight.ScheduledFireTime);

        var month = _calculator.Calculate(
            DailyAt(new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 31, 23, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 9, 0, 0, TimeSpan.Zero), month.ScheduledFireTime);

        var year = _calculator.Calculate(
            DailyAt(new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 12, 31, 23, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.Zero), year.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_LeapYearFebruaryBoundaries_ReturnCorrectDates()
    {
        var leapYear = _calculator.Calculate(
            DailyAt(new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 2, 28, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 9, 0, 0, TimeSpan.Zero), leapYear.ScheduledFireTime);

        var commonYear = _calculator.Calculate(
            DailyAt(new TimeOnly(9, 0)),
            new DateTimeOffset(2023, 2, 28, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero), commonYear.ScheduledFireTime);

        var afterLeapDay = _calculator.Calculate(
            DailyAt(new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 2, 29, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.Zero), afterLeapDay.ScheduledFireTime);
    }

    [Fact]
    public void Countdown_WhenNowHasDifferentOffsetsButSameUtcInstant_ReturnsIdenticalResult()
    {
        var definition = Countdown(
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(3));

        var withZeroOffset = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        var withNegativeOffset = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 7, 0, 0, TimeSpan.FromHours(-5)),
            TimeZoneInfo.Utc);

        Assert.Equal(withZeroOffset, withNegativeOffset);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 13, 0, 0, TimeSpan.Zero), withZeroOffset.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_WithNonSystemTimeZone_ConvertsCorrectly()
    {
        var fixedZone = TimeZoneInfo.CreateCustomTimeZone(
            "Test Plus Nine Thirty",
            TimeSpan.FromHours(9.5),
            "Test Plus Nine Thirty",
            "Test Plus Nine Thirty");

        // now = 2024-01-15T02:00Z -> local 2024-01-15 11:30, today 09:00 local already passed.
        var result = _calculator.Calculate(
            DailyAt(new TimeOnly(9, 0)),
            new DateTimeOffset(2024, 1, 15, 2, 0, 0, TimeSpan.Zero),
            fixedZone);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 23, 30, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_WhenTodayTargetDoesNotExist_ReturnsInvalidLocalTime()
    {
        // Pacific DST starts 2024-03-10 02:00 local; 02:30 local does not exist that day.
        var result = _calculator.Calculate(
            DailyAt(new TimeOnly(2, 30)),
            new DateTimeOffset(2024, 3, 10, 8, 0, 0, TimeSpan.Zero),
            PacificTime);

        Assert.Equal(NextExecutionStatus.InvalidLocalTime, result.Status);
        Assert.Null(result.ScheduledFireTime);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public void DailyAt_WhenTomorrowTargetDoesNotExist_ReturnsInvalidLocalTime()
    {
        // Today (2024-03-09) 02:30 is valid but passed; tomorrow 02:30 is skipped by DST.
        var result = _calculator.Calculate(
            DailyAt(new TimeOnly(2, 30)),
            new DateTimeOffset(2024, 3, 10, 0, 0, 0, TimeSpan.Zero),
            PacificTime);

        Assert.Equal(NextExecutionStatus.InvalidLocalTime, result.Status);
        Assert.Null(result.ScheduledFireTime);
    }

    [Fact]
    public void DailyAt_WhenLocalTimeIsAmbiguous_ChoosesLaterUtcInstant()
    {
        // Pacific DST ends 2024-11-03 02:00 local; 01:30 local occurs twice.
        // 01:30 PDT (-07:00) = 08:30Z, 01:30 PST (-08:00) = 09:30Z. The later instant wins.
        var result = _calculator.Calculate(
            DailyAt(new TimeOnly(1, 30)),
            new DateTimeOffset(2024, 11, 2, 20, 0, 0, TimeSpan.Zero),
            PacificTime);

        Assert.True(result.Succeeded);
        Assert.Equal(new DateTimeOffset(2024, 11, 3, 9, 30, 0, TimeSpan.Zero), result.ScheduledFireTime);
    }

    [Theory]
    [InlineData(TaskKind.Unknown)]
    [InlineData((TaskKind)99)]
    public void Calculate_WhenTaskKindIsUnknownOrUndefined_ReturnsInvalidTaskKind(TaskKind kind)
    {
        var definition = new TaskDefinition
        {
            Kind = kind,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };

        var result = _calculator.Calculate(
            definition,
            new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(NextExecutionStatus.InvalidTaskKind, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.ScheduledFireTime);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public void Calculate_WhenDefinitionIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _calculator.Calculate(
                null!,
                new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
                TimeZoneInfo.Utc));
    }

    [Fact]
    public void Calculate_WhenTimeZoneIsNull_ThrowsArgumentNullException()
    {
        var definition = Countdown(
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(2));

        Assert.Throws<ArgumentNullException>(() =>
            _calculator.Calculate(
                definition,
                new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
                null!));
    }

    [Fact]
    public void Calculate_UnrelatedFieldsDoNotChangeResult()
    {
        var definition = new TaskDefinition
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.Countdown,
            Action = PowerAction.Shutdown,
            CountdownDuration = TimeSpan.FromHours(2),
            WarningSeconds = 60,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };
        var unrelated = definition with
        {
            Id = Guid.NewGuid(),
            Action = PowerAction.Sleep,
            WarningSeconds = 300
        };
        var now = new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

        var first = _calculator.Calculate(definition, now, TimeZoneInfo.Utc);
        var second = _calculator.Calculate(unrelated, now, TimeZoneInfo.Utc);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Calculate_SuccessHasZeroOffsetAndFailureHasNullTimeAndNonEmptyMessage()
    {
        var success = _calculator.Calculate(
            DailyAt(new TimeOnly(15, 0)),
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.Zero, success.ScheduledFireTime!.Value.Offset);

        var failure = _calculator.Calculate(
            Countdown(new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero), null),
            new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Null(failure.ScheduledFireTime);
        Assert.False(string.IsNullOrEmpty(failure.Message));
    }

    [Fact]
    public void Calculate_RepeatedCallsWithSameInputReturnIdenticalResults()
    {
        var definition = DailyAt(new TimeOnly(15, 0));
        var now = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);

        var first = _calculator.Calculate(definition, now, TimeZoneInfo.Utc);
        var second = _calculator.Calculate(definition, now, TimeZoneInfo.Utc);

        Assert.Equal(first, second);

        var missingDefinition = Countdown(
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            null);
        var firstMissing = _calculator.Calculate(missingDefinition, now, TimeZoneInfo.Utc);
        var secondMissing = _calculator.Calculate(missingDefinition, now, TimeZoneInfo.Utc);

        Assert.Equal(firstMissing, secondMissing);
    }

    private static TaskDefinition Countdown(DateTimeOffset createdAt, TimeSpan? duration) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TaskKind.Countdown,
        Action = PowerAction.Shutdown,
        CountdownDuration = duration,
        CreatedAt = createdAt
    };

    private static TaskDefinition TodayAt(TimeOnly? target) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TaskKind.TodayAt,
        Action = PowerAction.Shutdown,
        TargetTimeOfDay = target,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static TaskDefinition DailyAt(TimeOnly? target) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TaskKind.DailyAt,
        Action = PowerAction.Shutdown,
        TargetTimeOfDay = target,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
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
