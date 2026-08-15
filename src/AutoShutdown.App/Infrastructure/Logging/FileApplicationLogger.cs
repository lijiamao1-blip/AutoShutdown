using System.Globalization;
using System.IO;
using System.Text;

namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// File-backed application logger.
///
/// Format per line:
///   yyyy-MM-ddTHH:mm:ss.fffzzz [Level] EventName Message
///
/// Files are written as UTF-8 without BOM. Every write opens the target file
/// with FileShare.ReadWrite so users can copy log files while the app runs.
/// A single lock serializes all writers so lines never interleave.
/// When the primary file exceeds the size limit it is rotated to .1.log
/// (shifting existing numbered files up so existing files are never overwritten).
/// Any I/O failure is swallowed by Log; it never affects business logic.
/// </summary>
public sealed class FileApplicationLogger : IApplicationLogger
{
    private const long DefaultMaxFileBytes = 5L * 1024 * 1024; // 5 MB
    private const int MaxRotationSlots = 64;

    private readonly string _logDirectory;
    private readonly ApplicationLogLevel _minLevel;
    private readonly Func<DateTimeOffset> _now;
    private readonly long _maxFileBytes;
    private readonly object _sync = new();

    private bool _disposed;

    public FileApplicationLogger(
        string logDirectory,
        ApplicationLogLevel minLevel = ApplicationLogLevel.Information,
        Func<DateTimeOffset>? clock = null,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(logDirectory);
        if (maxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        }

        _logDirectory = logDirectory;
        _minLevel = minLevel;
        _now = clock ?? (() => DateTimeOffset.Now);
        _maxFileBytes = maxFileBytes;
    }

    public string LogDirectory => _logDirectory;

    public bool IsEnabled(ApplicationLogLevel level) => !_disposed && level >= _minLevel;

    public void Log(
        ApplicationLogLevel level,
        string eventName,
        string message,
        Exception? exception = null)
    {
        if (_disposed || level < _minLevel)
        {
            return;
        }

        try
        {
            // The entire path (clock, entry creation, formatting, directory
            // creation, rotation, write and flush) is guarded. Any failure
            // silently degrades and never reaches the business layer.
            var entry = new ApplicationLogEntry(_now(), level, eventName, message, exception);
            Write(entry);
        }
        catch
        {
            // Logging must never crash the application. Silently degrade.
            // Deliberately no recursion back into the logger here.
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void Write(ApplicationLogEntry entry)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_logDirectory);

            var filePath = Path.Combine(
                _logDirectory,
                $"autoshutdown-{entry.Timestamp:yyyy-MM-dd}.log");

            var line = Format(entry);
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 2; // line + newline

            var currentLength = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            if (currentLength + lineBytes > _maxFileBytes)
            {
                Rotate(filePath);
            }

            using var stream = new FileStream(
                filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(line);
            writer.Write(writer.NewLine);
            writer.Flush();
        }
    }

    private void Rotate(string logFilePath)
    {
        // autoshutdown-2026-08-11.log -> autoshutdown-2026-08-11.1.log
        // Existing numbered files shift up (.1 -> .2, ...) so nothing is overwritten.
        var directory = Path.GetDirectoryName(logFilePath)!;
        var baseName = Path.GetFileNameWithoutExtension(logFilePath);
        var extension = Path.GetExtension(logFilePath);

        for (var slot = MaxRotationSlots; slot >= 1; slot--)
        {
            var source = Path.Combine(directory, $"{baseName}.{slot}{extension}");
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(directory, $"{baseName}.{slot + 1}{extension}");
            if (File.Exists(destination))
            {
                File.Delete(destination); // Only reachable beyond the defensive cap.
            }

            File.Move(source, destination);
        }

        File.Move(
            logFilePath,
            Path.Combine(directory, $"{baseName}.1{extension}"));
    }

    private static string Format(ApplicationLogEntry entry)
    {
        var timestamp = entry.Timestamp.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
            CultureInfo.InvariantCulture);

        // Every user-supplied fragment is sanitized so that one event always
        // produces exactly one physical log line: embedded \r and \n are
        // escaped instead of becoming orphan lines without a prefix.
        var line = string.Concat(
            timestamp,
            " [",
            entry.Level.ToString(),
            "] ",
            Sanitize(entry.EventName),
            " ",
            Sanitize(entry.Message));

        if (entry.Exception is not null)
        {
            line += " " + entry.Exception.GetType().FullName
                + ": " + Sanitize(entry.Exception.Message);

            if (entry.Exception.StackTrace is not null)
            {
                var frames = entry.Exception.StackTrace
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Take(3);
                line += " " + Sanitize(string.Join(" | ", frames));
            }
        }

        return line;
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '\r' or '\n')
            {
                builder ??= new StringBuilder(text.Length + 8);
                if (builder.Length == 0)
                {
                    builder.Append(text, 0, i);
                }

                builder.Append(ch == '\r' ? "\\r" : "\\n");
            }
            else
            {
                builder?.Append(ch);
            }
        }

        return builder?.ToString() ?? text;
    }
}
