namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// A single structured log entry. Immutable.
/// </summary>
public sealed record ApplicationLogEntry(
    DateTimeOffset Timestamp,
    ApplicationLogLevel Level,
    string EventName,
    string Message,
    Exception? Exception);
