using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling;

/// <summary>
/// 纯函数下次执行计算器（统一契约）：输入任务排程配置、now 与 TimeZoneInfo，
/// 输出下次触发时刻（DateTimeOffset?，null/失败表示无后续触发）。不读系统当前时间，
/// 不依赖机器区域隐式转换。S14 扩展 Weekdays / NextWorkday / NthWorkdayOfMonth /
/// OneTime 规则，并加入节假日例外；DST 缺失时刻向后移动到首个有效本地时刻，
/// 重复时刻选择较晚的 UTC 实例（避免提前执行电源动作）。
/// </summary>
public sealed class NextExecutionCalculator : INextExecutionCalculator
{
    private const int MaxSearchDays = 366;
    private const int MaxForwardMinutes = 180;

    public NextExecutionResult Calculate(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(timeZone);

        return definition.Kind switch
        {
            TaskKind.Countdown => CalculateCountdown(definition, now),
            TaskKind.TodayAt => CalculateTodayAt(definition, now, timeZone),
            TaskKind.DailyAt => CalculateDailyAt(definition, now, timeZone),
            TaskKind.Weekdays => CalculateWeekdays(definition, now, timeZone),
            TaskKind.NextWorkday => CalculateNextWorkday(definition, now, timeZone),
            TaskKind.NthWorkdayOfMonth => CalculateNthWorkdayOfMonth(definition, now, timeZone),
            TaskKind.OneTime => CalculateOneTime(definition, now, timeZone),
            _ => Failure(
                NextExecutionStatus.InvalidTaskKind,
                $"Task kind {definition.Kind} is not supported.")
        };
    }

    private static NextExecutionResult CalculateCountdown(
        TaskDefinition definition,
        DateTimeOffset now)
    {
        if (definition.CountdownDuration is null)
        {
            return Failure(
                NextExecutionStatus.MissingCountdownDuration,
                "Countdown tasks require CountdownDuration.");
        }

        if (definition.CountdownDuration.Value <= TimeSpan.Zero)
        {
            return Failure(
                NextExecutionStatus.InvalidCountdownDuration,
                "CountdownDuration must be strictly positive.");
        }

        var fireTime = (definition.CreatedAt + definition.CountdownDuration.Value).ToUniversalTime();
        if (fireTime <= now)
        {
            return Failure(
                NextExecutionStatus.NoFutureOccurrence,
                "The countdown target time is not in the future.");
        }

        return Success(fireTime);
    }

    private static NextExecutionResult CalculateTodayAt(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (definition.TargetTimeOfDay is null)
        {
            return Failure(
                NextExecutionStatus.MissingTargetTimeOfDay,
                "TodayAt tasks require TargetTimeOfDay.");
        }

        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var convertError = TryConvertLocalTarget(
            localNow.Date,
            definition.TargetTimeOfDay.Value,
            timeZone,
            out var fireTime);

        if (convertError is not null)
        {
            return convertError;
        }

        if (fireTime <= now)
        {
            return Failure(
                NextExecutionStatus.NoFutureOccurrence,
                "Today's target time is not in the future.");
        }

        return Success(fireTime);
    }

    private static NextExecutionResult CalculateDailyAt(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (definition.TargetTimeOfDay is null)
        {
            return Failure(
                NextExecutionStatus.MissingTargetTimeOfDay,
                "DailyAt tasks require TargetTimeOfDay.");
        }

        var holidays = definition.HolidayDates;
        var target = definition.TargetTimeOfDay.Value;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        // 从今天起向后寻找首个非节假日日；无节假日时行为与 S13 完全一致。
        for (var day = 0; day <= MaxSearchDays; day++)
        {
            var date = localNow.Date.AddDays(day);
            if (IsHoliday(date, holidays))
            {
                continue;
            }

            var error = TryConvertLocalTarget(date, target, timeZone, out var fireTime);
            if (error is not null)
            {
                return error;
            }

            if (fireTime > now)
            {
                return Success(fireTime);
            }
        }

        return Failure(
            NextExecutionStatus.NoFutureOccurrence,
            "No future daily occurrence was found.");
    }

    private static NextExecutionResult CalculateWeekdays(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (definition.Weekdays is null)
        {
            return Failure(
                NextExecutionStatus.MissingWeekdays,
                "Weekdays tasks require a Weekdays collection.");
        }

        if (definition.Weekdays.Count == 0)
        {
            return Failure(
                NextExecutionStatus.InvalidWeekdays,
                "Weekdays must contain at least one weekday.");
        }

        if (definition.TargetTimeOfDay is null)
        {
            return Failure(
                NextExecutionStatus.MissingTargetTimeOfDay,
                "Weekdays tasks require TargetTimeOfDay.");
        }

        var target = definition.TargetTimeOfDay.Value;
        var holidays = definition.HolidayDates;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        for (var day = 0; day <= MaxSearchDays; day++)
        {
            var date = localNow.Date.AddDays(day);
            if (!definition.Weekdays.Contains(date.DayOfWeek))
            {
                continue;
            }

            if (IsHoliday(date, holidays))
            {
                continue;
            }

            var fireTime = ConvertLocalTargetForward(AtTimeOn(date, target), timeZone);
            if (fireTime is null)
            {
                return Failure(
                    NextExecutionStatus.InvalidLocalTime,
                    $"The local time {AtTimeOn(date, target):yyyy-MM-dd HH:mm:ss} does not exist in the given time zone.");
            }

            if (fireTime.Value > now)
            {
                return Success(fireTime.Value);
            }
        }

        return Failure(
            NextExecutionStatus.NoWorkdayOccurrence,
            "No future weekday occurrence was found.");
    }

    private static NextExecutionResult CalculateNextWorkday(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (definition.TargetTimeOfDay is null)
        {
            return Failure(
                NextExecutionStatus.MissingTargetTimeOfDay,
                "NextWorkday tasks require TargetTimeOfDay.");
        }

        var target = definition.TargetTimeOfDay.Value;
        var holidays = definition.HolidayDates;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        for (var day = 0; day <= MaxSearchDays; day++)
        {
            var date = localNow.Date.AddDays(day);
            if (!IsWorkday(date, holidays))
            {
                continue;
            }

            var fireTime = ConvertLocalTargetForward(AtTimeOn(date, target), timeZone);
            if (fireTime is null)
            {
                return Failure(
                    NextExecutionStatus.InvalidLocalTime,
                    $"The local time {AtTimeOn(date, target):yyyy-MM-dd HH:mm:ss} does not exist in the given time zone.");
            }

            if (fireTime.Value > now)
            {
                return Success(fireTime.Value);
            }
        }

        return Failure(
            NextExecutionStatus.NoWorkdayOccurrence,
            "No future workday occurrence was found.");
    }

    private static NextExecutionResult CalculateNthWorkdayOfMonth(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (definition.NthWorkday is null)
        {
            return Failure(
                NextExecutionStatus.MissingNthWorkday,
                "NthWorkdayOfMonth tasks require NthWorkday.");
        }

        if (definition.NthWorkday.Value is < 1 or > 23)
        {
            return Failure(
                NextExecutionStatus.InvalidNthWorkday,
                "NthWorkday must be between 1 and 23.");
        }

        if (definition.TargetTimeOfDay is null)
        {
            return Failure(
                NextExecutionStatus.MissingTargetTimeOfDay,
                "NthWorkdayOfMonth tasks require TargetTimeOfDay.");
        }

        var n = definition.NthWorkday.Value;
        var target = definition.TargetTimeOfDay.Value;
        var holidays = definition.HolidayDates;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        for (var monthOffset = 0; monthOffset <= 24; monthOffset++)
        {
            var monthStart = new DateTime(localNow.Year, localNow.Month, 1).AddMonths(monthOffset);
            var nthWorkday = FindNthWorkday(monthStart, n, holidays);
            if (nthWorkday is null)
            {
                continue;
            }

            var fireTime = ConvertLocalTargetForward(AtTimeOn(nthWorkday.Value, target), timeZone);
            if (fireTime is null)
            {
                return Failure(
                    NextExecutionStatus.InvalidLocalTime,
                    $"The local time {AtTimeOn(nthWorkday.Value, target):yyyy-MM-dd HH:mm:ss} does not exist in the given time zone.");
            }

            if (fireTime.Value > now)
            {
                return Success(fireTime.Value);
            }
        }

        return Failure(
            NextExecutionStatus.NoWorkdayOccurrence,
            "No future Nth-workday occurrence was found.");
    }

    private static NextExecutionResult CalculateOneTime(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (definition.OneTimeDateTime is null)
        {
            return Failure(
                NextExecutionStatus.MissingOneTimeDateTime,
                "OneTime tasks require a precise date and time.");
        }

        var localTarget = AsLocalWallTime(definition.OneTimeDateTime.Value);
        var fireTime = ConvertLocalTargetForward(localTarget, timeZone);
        if (fireTime is null)
        {
            return Failure(
                NextExecutionStatus.InvalidLocalTime,
                $"The local time {localTarget:yyyy-MM-dd HH:mm:ss} does not exist in the given time zone.");
        }

        // 一次性日期不因节假日自动改期；已过期则返回 null 并标记 cancelled（不追溯执行）。
        if (fireTime.Value <= now)
        {
            return Failure(
                NextExecutionStatus.OneTimeExpired,
                "The one-time date and time has already passed.");
        }

        return Success(fireTime.Value);
    }

    private static NextExecutionResult? TryConvertLocalTarget(
        DateTime localDate,
        TimeOnly target,
        TimeZoneInfo timeZone,
        out DateTimeOffset fireTime)
    {
        var localTarget = new DateTime(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            target.Hour,
            target.Minute,
            target.Second,
            target.Millisecond,
            DateTimeKind.Unspecified);

        var offset = timeZone.GetUtcOffset(localTarget);
        var utcCandidate = new DateTime(localTarget.Ticks - offset.Ticks, DateTimeKind.Utc);

        if (TimeZoneInfo.ConvertTime(utcCandidate, timeZone) != localTarget)
        {
            fireTime = default;
            return Failure(
                NextExecutionStatus.InvalidLocalTime,
                $"The local time {localTarget:yyyy-MM-dd HH:mm:ss} does not exist in the given time zone.");
        }

        if (timeZone.IsAmbiguousTime(localTarget))
        {
            var latestUtc = timeZone.GetAmbiguousTimeOffsets(localTarget)
                .Select(candidateOffset => new DateTimeOffset(localTarget, candidateOffset).UtcDateTime)
                .Max();

            fireTime = new DateTimeOffset(latestUtc, TimeSpan.Zero);
            return null;
        }

        fireTime = new DateTimeOffset(localTarget, offset).ToUniversalTime();
        return null;
    }

    /// <summary>
    /// DST 缺失时刻向后移动到首个有效本地时刻（S14 统一契约）；重复时刻选择较晚的 UTC 实例。
    /// 与 <see cref="TryConvertLocalTarget"/> 的区别：缺失时刻不再返回 InvalidLocalTime，
    /// 而是前移；前移超过安全上限仍无效则返回 null。
    /// </summary>
    private static DateTimeOffset? ConvertLocalTargetForward(DateTime localTarget, TimeZoneInfo timeZone)
    {
        var candidate = AsLocalWallTime(localTarget);
        for (var i = 0; i <= MaxForwardMinutes; i++)
        {
            if (timeZone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (timeZone.IsAmbiguousTime(candidate))
            {
                var latestUtc = timeZone.GetAmbiguousTimeOffsets(candidate)
                    .Select(offset => new DateTimeOffset(candidate, offset).UtcDateTime)
                    .Max();
                return new DateTimeOffset(latestUtc, TimeSpan.Zero);
            }

            var offset = timeZone.GetUtcOffset(candidate);
            return new DateTimeOffset(candidate, offset).ToUniversalTime();
        }

        return null;
    }

    private static DateTime? FindNthWorkday(
        DateTime monthStart,
        int n,
        IReadOnlyList<DateOnly>? holidays)
    {
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var count = 0;
        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(monthStart.Year, monthStart.Month, day);
            if (!IsWorkday(date, holidays))
            {
                continue;
            }

            count++;
            if (count == n)
            {
                return date;
            }
        }

        return null;
    }

    private static bool IsWorkday(DateTime date, IReadOnlyList<DateOnly>? holidays)
        => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && !IsHoliday(date, holidays);

    private static bool IsHoliday(DateTime date, IReadOnlyList<DateOnly>? holidays)
    {
        if (holidays is null || holidays.Count == 0)
        {
            return false;
        }

        return holidays.Contains(DateOnly.FromDateTime(date));
    }

    private static DateTime AtTimeOn(DateTime date, TimeOnly time)
        => new(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, time.Millisecond, DateTimeKind.Unspecified);

    private static DateTime AsLocalWallTime(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Millisecond, DateTimeKind.Unspecified);

    private static NextExecutionResult Success(DateTimeOffset fireTime) => new()
    {
        Status = NextExecutionStatus.Success,
        ScheduledFireTime = fireTime,
        Message = $"The next execution is scheduled for {fireTime:O} (UTC)."
    };

    private static NextExecutionResult Failure(
        NextExecutionStatus status,
        string message) => new()
        {
            Status = status,
            Message = message
        };
}
