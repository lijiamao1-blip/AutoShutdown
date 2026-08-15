namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// Application-level logging contract. Implementations must never throw
/// from Log; logging failures must be swallowed so business logic is unaffected.
/// </summary>
public interface IApplicationLogger : IDisposable
{
    /// <summary>Gets the directory where log files are written.</summary>
    string LogDirectory { get; }

    /// <summary>Returns true when the given level would be recorded.</summary>
    bool IsEnabled(ApplicationLogLevel level);

    /// <summary>
    /// Records an entry. Must not throw and must not recurse into the logger
    /// when the underlying sink fails.
    /// </summary>
    void Log(
        ApplicationLogLevel level,
        string eventName,
        string message,
        Exception? exception = null);
}

public static class ApplicationLogExtensions
{
    public static void Debug(this IApplicationLogger logger, string eventName, string message)
        => logger.Log(ApplicationLogLevel.Debug, eventName, message);

    public static void Info(this IApplicationLogger logger, string eventName, string message)
        => logger.Log(ApplicationLogLevel.Information, eventName, message);

    public static void Warning(this IApplicationLogger logger, string eventName, string message)
        => logger.Log(ApplicationLogLevel.Warning, eventName, message);

    public static void Error(this IApplicationLogger logger, string eventName, string message)
        => logger.Log(ApplicationLogLevel.Error, eventName, message);

    public static void Error(
        this IApplicationLogger logger,
        string eventName,
        string message,
        Exception exception)
        => logger.Log(ApplicationLogLevel.Error, eventName, message, exception);
}
