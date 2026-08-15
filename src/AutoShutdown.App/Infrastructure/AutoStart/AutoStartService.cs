using System.IO;

namespace AutoShutdown.App.Infrastructure.AutoStart;

/// <summary>
/// 开机自启动服务实现。路径取自当前进程可执行文件（Environment.ProcessPath），
/// 不硬编码任何目录。所有方法均不抛异常：失败以 AutoStartOperationResult 返回，
/// 参数或内部异常不得伪装为成功。启用前路径无效时默认拒绝。
/// </summary>
public sealed class AutoStartService : IAutoStartService
{
    public const string ResultCodeOk = "OK";
    public const string ResultCodePathUnavailable = "PathUnavailable";
    public const string ResultCodeEnableFailed = "EnableFailed";
    public const string ResultCodeDisableFailed = "DisableFailed";
    public const string ResultCodeRepairFailed = "RepairFailed";
    public const string ResultCodeRepairNotAllowed = "RepairNotAllowed";

    private readonly IRegistryRunKeyStore _store;
    private readonly Func<string?> _pathProvider;

    public AutoStartService(
        IRegistryRunKeyStore store,
        Func<string?>? pathProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _pathProvider = pathProvider ?? (() => Environment.ProcessPath);
    }

    public AutoStartStatus GetStatus()
    {
        try
        {
            return Evaluate(_store.GetValue());
        }
        catch
        {
            // 存储读取失败：不可用。不得把失败伪装为成功。
            return AutoStartStatus.Unavailable;
        }
    }

    public AutoStartOperationResult Enable()
    {
        if (!TryGetValidExecutablePath(out var path, out var pathError))
        {
            return AutoStartOperationResult.Failure(
                ResultCodePathUnavailable,
                pathError,
                GetStatus());
        }

        // 已正确启用时返回成功，不重复制造副作用。
        if (GetStatus() == AutoStartStatus.Enabled)
        {
            return AutoStartOperationResult.Success(
                ResultCodeOk,
                "开机自启动已启用。",
                AutoStartStatus.Enabled);
        }

        try
        {
            _store.SetValue(Quote(path));
            return AutoStartOperationResult.Success(
                ResultCodeOk,
                "开机自启动已启用。",
                AutoStartStatus.Enabled);
        }
        catch
        {
            return AutoStartOperationResult.Failure(
                ResultCodeEnableFailed,
                "写入自启动注册项失败。",
                GetStatus());
        }
    }

    public AutoStartOperationResult Disable()
    {
        // 注册项不存在时直接幂等成功，不产生任何删除操作。
        // 仅当注册项实际存在（或状态无法确认）时才调用 DeleteValue。
        if (GetStatus() == AutoStartStatus.Disabled)
        {
            return AutoStartOperationResult.Success(
                ResultCodeOk,
                "开机自启动已关闭。",
                AutoStartStatus.Disabled);
        }

        try
        {
            // 仅删除固定注册项 AutoShutdown.Desktop；值不存在时也视为成功。
            _store.DeleteValue();
            return AutoStartOperationResult.Success(
                ResultCodeOk,
                "开机自启动已关闭。",
                AutoStartStatus.Disabled);
        }
        catch
        {
            return AutoStartOperationResult.Failure(
                ResultCodeDisableFailed,
                "删除自启动注册项失败。",
                GetStatus());
        }
    }

    public AutoStartOperationResult Repair()
    {
        // 仅允许修复"本软件已有但错误"的注册项；Disabled/Enabled/Unavailable
        // 一律拒绝且零写入，不得通过 Repair 暗中启用或改变正常状态。
        var status = GetStatus();
        if (status is not (AutoStartStatus.PathMismatch or AutoStartStatus.InvalidValue))
        {
            var reason = status switch
            {
                AutoStartStatus.Disabled => "自启动未启用，无需修复。",
                AutoStartStatus.Enabled => "自启动已正确启用，无需修复。",
                _ => "无法确认自启动状态，已拒绝修复。"
            };
            return AutoStartOperationResult.Failure(
                ResultCodeRepairNotAllowed,
                reason,
                status);
        }

        if (!TryGetValidExecutablePath(out var path, out var pathError))
        {
            return AutoStartOperationResult.Failure(
                ResultCodePathUnavailable,
                pathError,
                status);
        }

        try
        {
            _store.SetValue(Quote(path));
            return AutoStartOperationResult.Success(
                ResultCodeOk,
                "自启动注册项已修复。",
                AutoStartStatus.Enabled);
        }
        catch
        {
            return AutoStartOperationResult.Failure(
                ResultCodeRepairFailed,
                "修复自启动注册项失败。",
                GetStatus());
        }
    }

    /// <summary>
    /// 判定注册值状态。比较正确处理引号、大小写（忽略）与规范化绝对路径；
    /// 项存在但路径不一致时返回 PathMismatch，绝不自动覆盖。
    /// 仅"注册项不存在"（raw 为 null）判定为 Disabled；注册项存在但内容为空、
    /// 纯空白、纯引号或无法解析的一律判定为 InvalidValue。
    /// </summary>
    private AutoStartStatus Evaluate(string? raw)
    {
        if (raw is null)
        {
            return AutoStartStatus.Disabled;
        }

        var candidate = Normalize(raw);
        if (candidate is null)
        {
            return AutoStartStatus.InvalidValue;
        }

        if (TryGetValidExecutablePath(out var current, out _)
            && string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
        {
            return AutoStartStatus.Enabled;
        }

        return AutoStartStatus.PathMismatch;
    }

    /// <summary>规范化注册值：剥离引号与空白并解析为绝对路径；无法解析返回 null。</summary>
    private static string? Normalize(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        trimmed = trimmed.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 校验并规范化当前进程路径：非空、无非法字符、扩展名为 .exe、文件存在。
    /// 任一不满足即拒绝（不写注册表）。
    /// </summary>
    private bool TryGetValidExecutablePath(out string path, out string error)
    {
        var candidate = _pathProvider();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            path = string.Empty;
            error = "无法确定程序路径。";
            return false;
        }

        if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
            error = "程序路径无效。";
            return false;
        }

        if (!File.Exists(candidate))
        {
            path = string.Empty;
            error = "程序文件不存在。";
            return false;
        }

        path = Path.GetFullPath(candidate);
        error = string.Empty;
        return true;
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
