using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.App.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
