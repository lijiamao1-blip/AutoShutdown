using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// Removes log files older than the retention window. Only files matching
/// the "autoshutdown-YYYY-MM-DD[.N].log" naming rule are considered; any
/// other file is left untouched. Files whose date cannot be parsed are kept.
/// Runs once at application start; never runs in a background loop.
/// Delete failures are ignored.
/// </summary>
public sealed class LogRetentionService
{
    private const int DefaultRetentionDays = 14;

    private static readonly Regex NamePattern = new(
        @"^autoshutdown-(?<date>\d{4}-\d{2}-\d{2})(?:\.\d+)?\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _logDirectory;
    private readonly IClock _clock;
    private readonly int _retentionDays;

    public LogRetentionService(string logDirectory, IClock clock, int retentionDays = DefaultRetentionDays)
    {
        ArgumentNullException.ThrowIfNull(logDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        if (retentionDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        _logDirectory = logDirectory;
        _clock = clock;
        _retentionDays = retentionDays;
    }

    public void RunOnce()
    {
        try
        {
            RunOnceCore();
        }
        catch
        {
            // Retention cleanup must never affect application startup, the
            // scheduler or the UI. Stop safely on any unexpected failure.
        }
    }

    private void RunOnceCore()
    {
        if (!Directory.Exists(_logDirectory))
        {
            return;
        }

        var cutoff = _clock.UtcNow.Date.AddDays(-_retentionDays);

        foreach (var file in Directory.EnumerateFiles(_logDirectory, "autoshutdown-*.log"))
        {
            try
            {
                DeleteIfExpired(file, cutoff);
            }
            catch
            {
                // A failing file must not stop cleanup; skip it and continue.
            }
        }
    }

    private void DeleteIfExpired(string file, DateTime cutoff)
    {
        var fileName = Path.GetFileName(file);
        var match = NamePattern.Match(fileName);
        if (!match.Success)
        {
            return; // Unrecognized name: keep.
        }

        if (!DateTime.TryParseExact(
                match.Groups["date"].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var fileDate))
        {
            return; // Unparseable date: keep.
        }

        if (fileDate < cutoff)
        {
            File.Delete(file);
        }
    }
}
