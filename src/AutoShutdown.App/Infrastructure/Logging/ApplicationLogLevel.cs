namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// Application log levels. Fixed to four levels; no Trace or Fatal.
/// Debug is not written by default in Release.
/// </summary>
public enum ApplicationLogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3
}
