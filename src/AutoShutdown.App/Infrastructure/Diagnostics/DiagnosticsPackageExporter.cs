using System.IO;
using System.IO.Compression;
using System.Text;
using AutoShutdown.App.Infrastructure.Logging;

namespace AutoShutdown.App.Infrastructure.Diagnostics;

/// <summary>任务摘要行（导出用）。不含用户输入的机器名/路径等隐私字段（由导出层统一控制）。</summary>
public sealed record DiagnosticsTaskRow(
    string KindText,
    string ActionText,
    string FireTimeText,
    string StateText,
    string ExtraText);

/// <summary>WoL 目标行（名称/MAC/广播/端口；隐私开关控制实际渲染值）。</summary>
public sealed record DiagnosticsWolTargetRow(
    string Name,
    string Mac,
    string BroadcastText,
    string PortText);

/// <summary>已配对设备（名称/id/配对时间；隐私开关控制实际渲染值）。</summary>
public sealed record DiagnosticsPairedDevice(
    string Name,
    string DeviceId,
    string PairedAtText);

/// <summary>远程控制状态快照（值一律在渲染层脱敏，PIN 值永不进入）。</summary>
public sealed record DiagnosticsRemoteStatus(
    bool Enabled,
    string ListenAddress,
    string ListenPortText,
    bool RequireTls,
    string PinPresenceText,
    IReadOnlyList<DiagnosticsPairedDevice> Devices);

/// <summary>导出内容快照。全部为只读数据；导出层负责渲染与脱敏。</summary>
public sealed record DiagnosticsExportContent(
    string AppName,
    string VersionText,
    string BuildCommitText,
    string SigningStatusText,
    string HeaderModeText,
    string ConfigStatusText,
    string SchedulerStatusText,
    string DataRoot,
    string LogDirectory,
    string ConfigJsonText,
    IReadOnlyList<DiagnosticsTaskRow> Tasks,
    IReadOnlyList<DiagnosticsWolTargetRow> WolTargets,
    DiagnosticsRemoteStatus Remote,
    IReadOnlyList<SelfCheckItem> SelfCheckItems,
    bool IncludePrivacyInfo,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// 导出脱敏诊断包（S-UI1）。用户显式点击导出后才生成 zip；默认排除 PIN/HMAC/配对 secret/
/// 证书私钥/PFX 密码（含持密钥的 dpapi/pfx 文件不纳入）；隐私信息（路径/IP/MAC/机器名）
/// 默认脱敏，仅当用户确认 <see cref="DiagnosticsExportContent.IncludePrivacyInfo"/> 时包含。
/// </summary>
public sealed class DiagnosticsPackageExporter
{
    public const int MaxLogFiles = 5;
    public const int MaxLogLinesPerFile = 2000;

    private const string Readme = "AutoShutdown 诊断包说明\n"
        + "此压缩包由用户主动点击「导出脱敏诊断包」生成。\n"
        + "默认已排除：PIN、HMAC/配对 secret、证书私钥、PFX 密码（含 remote-server-cert.dpapi 等持密钥文件）。\n"
        + "隐私信息（路径、IP、MAC、机器名）：导出前勾选「包含隐私信息」才会包含，否则全部脱敏。\n"
        + "本包只用于故障排查；请勿在公共渠道分享。\n";

    private readonly IApplicationLogger _logger;

    public DiagnosticsPackageExporter(IApplicationLogger logger)
    {
        _logger = logger;
    }

    /// <summary>生成诊断摘要文本（复制摘要与 zip 内 summary.txt 共用同一来源，保证一致）。</summary>
    public string BuildSummaryText(DiagnosticsExportContent content)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"产品：{content.AppName}");
        builder.AppendLine($"版本：{content.VersionText}");
        builder.AppendLine($"构建提交：{content.BuildCommitText}");
        builder.AppendLine($"候选包签名：{content.SigningStatusText}");
        builder.AppendLine($"运行模式：{content.HeaderModeText}");
        builder.AppendLine($"配置状态：{content.ConfigStatusText}");
        builder.AppendLine($"调度服务：{content.SchedulerStatusText}");
        builder.AppendLine($"数据目录：{MaskPath(content.DataRoot, content.IncludePrivacyInfo)}");
        builder.AppendLine($"日志目录：{MaskPath(content.LogDirectory, content.IncludePrivacyInfo)}");
        builder.AppendLine($"本地任务数：{content.Tasks.Count}");
        builder.AppendLine($"WoL 目标数：{content.WolTargets.Count}");
        builder.AppendLine($"远程控制：{(content.Remote.Enabled ? "已启用" : "未启用")}；TLS {(content.Remote.RequireTls ? "强制" : "未强制")}");
        builder.AppendLine($"包含隐私信息：{(content.IncludePrivacyInfo ? "是（用户确认）" : "否（已脱敏）")}");
        builder.AppendLine($"生成时间：{content.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}（本地时区）");
        return builder.ToString();
    }

    /// <summary>导出诊断包到 zipPath；成功返回 true，失败返回 false 并写错误日志。</summary>
    public async Task<bool> ExportAsync(
        DiagnosticsExportContent content,
        string zipPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(zipPath);

        try
        {
            var directory = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "README.txt", Readme, cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, "summary.txt", BuildSummaryText(content), cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, "self-check.txt", BuildSelfCheckText(content), cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, "config-redacted.json", DiagnosticsRedactor.RedactFile(content.ConfigJsonText, content.IncludePrivacyInfo), cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, "tasks.txt", BuildTasksText(content), cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, "wol-targets.txt", BuildWolTargetsText(content), cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, "remote-status.txt", BuildRemoteStatusText(content), cancellationToken).ConfigureAwait(false);

                foreach (var (name, text) in CollectLogFiles(content.LogDirectory, cancellationToken))
                {
                    await WriteEntryAsync(
                        archive,
                        "logs/" + name,
                        DiagnosticsRedactor.Redact(text, content.IncludePrivacyInfo),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.Info("DiagnosticsExport", $"诊断包已导出：{zipPath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            _logger.Error("DiagnosticsExport", "导出诊断包失败", exception);
            return false;
        }
    }

    private static string BuildSelfCheckText(DiagnosticsExportContent content)
    {
        var builder = new StringBuilder();
        builder.AppendLine("=== 安全自检（只读，未自动修复） ===");
        foreach (var item in content.SelfCheckItems)
        {
            builder.AppendLine($"- [{item.Name}] {(item.IsHealthy ? "通过" : "关注")}：{item.Detail}");
        }

        return builder.ToString();
    }

    private static string BuildTasksText(DiagnosticsExportContent content)
    {
        if (content.Tasks.Count == 0)
        {
            return "暂无本地任务。";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"共 {content.Tasks.Count} 个本地任务（不含用户输入的任务名/机器名；如需包含请在导出前勾选「包含隐私信息」）。");
        for (var i = 0; i < content.Tasks.Count; i++)
        {
            var task = content.Tasks[i];
            var label = content.IncludePrivacyInfo ? $"任务 {i + 1}" : $"任务 {i + 1}";
            builder.AppendLine($"{label}：{task.KindText} / {task.ActionText} / 触发 {task.FireTimeText} / 状态 {task.StateText}{(string.IsNullOrEmpty(task.ExtraText) ? string.Empty : $" / {task.ExtraText}")}");
        }

        return builder.ToString();
    }

    private static string BuildWolTargetsText(DiagnosticsExportContent content)
    {
        if (content.WolTargets.Count == 0)
        {
            return "未配置 WoL 目标。";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"共 {content.WolTargets.Count} 个 WoL 目标（仅局域网；默认脱敏机器名/MAC/广播地址）。");
        for (var i = 0; i < content.WolTargets.Count; i++)
        {
            var target = content.WolTargets[i];
            var name = content.IncludePrivacyInfo ? target.Name : "目标 N";
            var mac = content.IncludePrivacyInfo ? target.Mac : "**:**:**:**:**:**";
            var broadcast = content.IncludePrivacyInfo
                ? target.BroadcastText
                : (string.IsNullOrEmpty(target.BroadcastText) ? "（默认 255.255.255.255）" : "[IP已脱敏]");
            var port = string.IsNullOrEmpty(target.PortText) ? "默认 9" : target.PortText;
            builder.AppendLine($"{name}：MAC {mac}；广播 {broadcast}；端口 {port}");
        }

        return builder.ToString();
    }

    private static string BuildRemoteStatusText(DiagnosticsExportContent content)
    {
        var remote = content.Remote;
        var builder = new StringBuilder();
        builder.AppendLine($"远程控制：{(remote.Enabled ? "已启用" : "未启用（默认安全，不监听）")}");
        builder.AppendLine($"强制 TLS：{(remote.RequireTls ? "是" : "否（明文被接受，风险提示）")}");
        builder.AppendLine($"监听地址：{MaskPath(remote.ListenAddress, content.IncludePrivacyInfo)}");
        builder.AppendLine($"监听端口：{remote.ListenPortText}");
        builder.AppendLine($"当前 PIN：{remote.PinPresenceText}（PIN 值永不进入诊断包）");
        builder.AppendLine($"已配对设备：{remote.Devices.Count}");
        for (var i = 0; i < remote.Devices.Count; i++)
        {
            var device = remote.Devices[i];
            var name = content.IncludePrivacyInfo ? device.Name : "设备 N";
            var id = content.IncludePrivacyInfo ? device.DeviceId : "[id已脱敏]";
            builder.AppendLine($"- {name}（{id}，配对于 {device.PairedAtText}）");
        }

        return builder.ToString();
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        string text,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using (var stream = entry.Open())
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>收集最近的日志文件内容（最多 <see cref="MaxLogFiles"/> 个、每个最多 <see cref="MaxLogLinesPerFile"/> 行），按修改时间倒序。</summary>
    private static List<(string Name, string Text)> CollectLogFiles(string logDirectory, CancellationToken cancellationToken)
    {
        var result = new List<(string, string)>();
        try
        {
            if (!Directory.Exists(logDirectory))
            {
                return result;
            }

            var files = Directory.GetFiles(logDirectory, "autoshutdown-*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .Take(MaxLogFiles)
                .ToList();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var builder = new StringBuilder();
                try
                {
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    string? line;
                    var count = 0;
                    while ((line = reader.ReadLine()) is not null && count < MaxLogLinesPerFile)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        builder.AppendLine(line);
                        count++;
                    }
                }
                catch (Exception)
                {
                    builder.AppendLine("（读取该日志文件失败，已跳过）");
                }

                result.Add((Path.GetFileName(file), builder.ToString()));
            }
        }
        catch (Exception)
        {
            // 日志收集失败不影响整个导出（已有条目照常打包）。
        }

        return result;
    }

    private static string MaskPath(string? value, bool includePrivacy)
        => includePrivacy || string.IsNullOrEmpty(value) ? value ?? string.Empty : "[路径已脱敏]";
}
