using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling;

public sealed class NextExecutionCalculator : INextExecutionCalculator
{
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

        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var todayError = TryConvertLocalTarget(
            localNow.Date,
            definition.TargetTimeOfDay.Value,
            timeZone,
            out var todayFireTime);

        if (todayError is not null)
        {
            return todayError;
        }

        if (todayFireTime > now)
        {
            return Success(todayFireTime);
        }

        var tomorrowError = TryConvertLocalTarget(
            localNow.Date.AddDays(1),
            definition.TargetTimeOfDay.Value,
            timeZone,
            out var tomorrowFireTime);

        if (tomorrowError is not null)
        {
            return tomorrowError;
        }

        return Success(tomorrowFireTime);
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
