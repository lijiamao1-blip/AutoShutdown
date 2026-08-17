using System.IO;
using System.Text;
using System.Text.Json;
using AutoShutdown.Core.Remote;

namespace AutoShutdown.App.Infrastructure.Remote;

/// <summary>
/// 远程层高危审计日志（S23 CP5）：JSON Lines 落盘，每日一个文件，行首 UTC 时间戳。
/// 双重防线保证不含敏感材料：<see cref="RemoteAuditEntry"/> 契约本身不含 PIN/secret/HMAC/私钥；
/// 本实现再对 message/字段做转义，任何换行/控制字符被转义，一条审计永远只占一行。
/// 审计写入失败静默降级（绝不影响远程业务/拒绝路径），同一锁串行化写入。
/// </summary>
public sealed class FileRemoteAuditLog : IRemoteAuditLog
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();

    public FileRemoteAuditLog(string directory, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string DirectoryPath => _directory;

    public void Write(RemoteAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                var path = System.IO.Path.Combine(
                    _directory,
                    "remote-audit-" + _now().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");

                var line = JsonSerializer.Serialize(new
                {
                    ts = entry.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    deviceId = Escape(entry.DeviceId),
                    sourceIp = Escape(entry.SourceIp),
                    method = Escape(entry.Method),
                    outcome = entry.Outcome.ToString(),
                    message = Escape(entry.Message)
                }) + Environment.NewLine;

                System.IO.File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // 审计失败静默降级，绝不把远程业务拖垮；不递归回本日志器。
        }
    }

    /// <summary>把可能含换行/控制字符的字段压成单行（防御：即使上层传入脏数据也不破坏行格式）。</summary>
    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch is '\r' or '\n' or '\t')
            {
                builder ??= new StringBuilder(text.Length + 8);
                if (builder.Length == 0)
                {
                    builder.Append(text, 0, index);
                }

                builder.Append(ch switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    _ => "\\t"
                });
            }
            else
            {
                builder?.Append(ch);
            }
        }

        return builder?.ToString() ?? text;
    }
}
