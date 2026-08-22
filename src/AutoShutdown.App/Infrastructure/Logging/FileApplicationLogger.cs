using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// File-backed application logger.
///
/// Format per line:
///   yyyy-MM-ddTHH:mm:ss.fffzzz [Level] EventName Message
///
/// Files are written as UTF-8 without BOM. Every write opens the target file
/// with FileShare.ReadWrite so users can copy log files while the app runs,
/// and FileShare.Delete so a concurrent cross-process rotation's File.Move
/// never fails against an in-flight writer (S-STARTUP-D1-D1).
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

    // S-STARTUP-D1：跨进程日志写锁。主实例运行期间，双击启动的次实例会短暂地向同一日志文件
    // 追加检测/转发日志；若两个进程的追加位置互相陈旧（各自在打开时刻记录 EOF），后写的进程
    // 可能覆盖先写进程的行（实测：次实例的「检测到主实例已在运行…」覆盖了主实例的
    // MainWindowActivated 行）。命名互斥锁保证同一时刻只有一个进程写日志文件。
    private static readonly Mutex _writeMutex = new(initiallyOwned: false, @"Local\AutoShutdown.Desktop.LogWriter.v1");

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
            // 整行（含换行）作为一个字节数组一次追加：单次写 + 写前重锚定 EOF，杜绝多进程
            // 并发追加时因陈旧 EOF 位置导致的行级覆盖/交错（见 _writeMutex 注释）。
            var lineBytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);

            var acquired = false;
            try
            {
                acquired = _writeMutex.WaitOne(TimeSpan.FromMilliseconds(500));
            }
            catch (AbandonedMutexException)
            {
                // 上一个进程在写日志时崩溃：互斥锁被放弃，本进程已获得所有权，可继续写。
                acquired = true;
            }
            catch
            {
                // 拿不到互斥锁绝不崩溃、绝不丢业务：退化为无锁追加（单次原子写仍尽力而为）。
                acquired = false;
            }

            try
            {
                // S-STARTUP-D1-D1：文件长度检查 + Rotate + 打开 + Seek + Write + Flush 全部纳入
                // 同一跨进程临界区（互斥锁内）。两进程并发逼近轮转阈值时轮转互斥、互不冲突；
                // 获取锁失败时绝不无锁 Rotate（否则两进程同时轮转会互相移动对方的文件）。
                if (acquired)
                {
                    var currentLength = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
                    if (currentLength + lineBytes.Length > _maxFileBytes)
                    {
                        Rotate(filePath);
                    }
                }

                // S-STARTUP-D1-D1：FileShare.Delete 使跨进程并发时 Rotate 的 File.Move 不会因
                // 另一进程（超时退化的无锁追加写）正持有 base 文件而抛 IOException —— 否则该
                // 临界区内写者的行会因移动失败被静默丢弃。共享 Delete 权限下移动仍成功，无锁
                // 写者的行落入被移动后的文件，绝不覆盖、绝不丢行。
                using var stream = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(0, SeekOrigin.End); // 写前重锚定到真实 EOF，缩小跨进程覆盖窗口
                stream.Write(lineBytes, 0, lineBytes.Length);
                stream.Flush(true); // 落盘，保证退出后日志可立即可靠读取
            }
            finally
            {
                if (acquired)
                {
                    try
                    {
                        _writeMutex.ReleaseMutex();
                    }
                    catch
                    {
                        // 释放失败仅影响后续写锁竞争，不影响本次写入。
                    }
                }
            }
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
