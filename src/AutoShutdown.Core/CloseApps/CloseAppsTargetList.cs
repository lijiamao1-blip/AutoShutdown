using AutoShutdown.Core.Configuration;

namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// CloseApps 目标清单校验与规范化（S18）。稳定标识只能为「可执行路径」或「进程 ID」，
/// 二者恰取其一；不得只按模糊标题匹配。强杀逐目标显式 opt-in（ForceKillAllowed 默认 false）。
/// 校验失败绝不静默回退为可能触发关闭/强杀的默认值。
/// S-CLOSEUI1-D3：路径目标必须是完整绝对路径，相对路径在配置校验阶段拒绝（fail-closed）。
/// </summary>
public static class CloseAppsTargetList
{
    public const int MinGracefulTimeoutSeconds = 1;
    public const int MaxGracefulTimeoutSeconds = 300;

    /// <summary>结构校验配置段，返回错误列表（空 = 合法）。</summary>
    public static IReadOnlyList<string> Validate(CloseAppsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        if (config.GracefulTimeoutSeconds is < MinGracefulTimeoutSeconds or > MaxGracefulTimeoutSeconds)
        {
            errors.Add(
                $"CloseApps.GracefulTimeoutSeconds must be between {MinGracefulTimeoutSeconds} and {MaxGracefulTimeoutSeconds}.");
        }

        if (config.Targets is null)
        {
            errors.Add("CloseApps.Targets must not be null.");
            return errors;
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPids = new HashSet<int>();

        for (var index = 0; index < config.Targets.Length; index++)
        {
            var target = config.Targets[index];
            var prefix = $"CloseApps.Targets[{index}]";

            if (target is null)
            {
                errors.Add($"{prefix} must not be null.");
                continue;
            }

            var hasPath = !string.IsNullOrWhiteSpace(target.ExecutablePath);
            var hasPid = target.ProcessId is > 0;

            if (hasPath && hasPid)
            {
                errors.Add($"{prefix} must specify exactly one of ExecutablePath or ProcessId.");
            }
            else if (!hasPath && !hasPid)
            {
                errors.Add($"{prefix} must specify exactly one of ExecutablePath or ProcessId.");
            }

            if (hasPid && target.ProcessId is <= 0)
            {
                errors.Add($"{prefix}.ProcessId must be positive.");
            }

            if (target.GracefulTimeoutSeconds is < MinGracefulTimeoutSeconds or > MaxGracefulTimeoutSeconds)
            {
                errors.Add(
                    $"{prefix}.GracefulTimeoutSeconds must be between {MinGracefulTimeoutSeconds} and {MaxGracefulTimeoutSeconds}.");
            }

            if (hasPath)
            {
                // S-CLOSEUI1-D3：可执行路径目标必须是完整绝对路径。相对/drive-relative/
                // 根相对路径依赖当前工作目录，绝不作为可执行关闭目标，在配置校验阶段
                // 拒绝（fail-closed），不等到执行时才处理；完整绝对路径仍按既有语义校验。
                var trimmedPath = target.ExecutablePath!.Trim();
                if (!Path.IsPathFullyQualified(trimmedPath))
                {
                    errors.Add($"{prefix}.ExecutablePath must be a fully qualified absolute path.");
                }
                else if (ExecutablePathKey.Normalize(trimmedPath) is null)
                {
                    // 完整绝对路径但含 NUL / 非法字符 / 无法规范化：同样 fail-closed。
                    errors.Add($"{prefix}.ExecutablePath must be a valid absolute path.");
                }

                if (!seenPaths.Add(target.ExecutablePath!))
                {
                    errors.Add($"{prefix}.ExecutablePath is duplicated.");
                }
            }

            if (hasPid)
            {
                if (!seenPids.Add(target.ProcessId!.Value))
                {
                    errors.Add($"{prefix}.ProcessId is duplicated.");
                }
            }
        }

        return errors;
    }

    /// <summary>把已校验配置规范化为目标列表；计算脱敏 TargetId 并把秒转 TimeSpan。</summary>
    public static IReadOnlyList<CloseAppTarget> Normalize(
        CloseAppsConfig config,
        TimeSpan defaultTimeout)
    {
        ArgumentNullException.ThrowIfNull(config);

        var targets = new List<CloseAppTarget>(config.Targets?.Length ?? 0);
        if (config.Targets is null)
        {
            return targets;
        }

        foreach (var item in config.Targets)
        {
            if (item is null)
            {
                continue;
            }

            var hasPath = !string.IsNullOrWhiteSpace(item.ExecutablePath);
            targets.Add(new CloseAppTarget
            {
                TargetId = hasPath
                    ? Path.GetFileName(item.ExecutablePath!.Trim())
                    : $"pid:{item.ProcessId}",
                ExecutablePath = hasPath ? item.ExecutablePath!.Trim() : null,
                ProcessId = item.ProcessId,
                ForceKillAllowed = item.ForceKillAllowed,
                GracefulTimeout = item.GracefulTimeoutSeconds is int seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : defaultTimeout
            });
        }

        return targets;
    }
}
