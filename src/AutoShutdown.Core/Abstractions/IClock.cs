namespace AutoShutdown.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    TimeZoneInfo LocalTimeZone { get; }
}
