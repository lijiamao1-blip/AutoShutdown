using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;

namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// CloseApps 关闭编排（S18）。按配置查找目标进程，先优雅关闭（WM_CLOSE）、有界等待退出，
/// 仅在逐目标明确授权（ForceKillAllowed）时强杀；强杀前复核 PID + 启动时间防 PID 重用。
/// 自身/系统关键/非当前会话进程绝不关闭；访问拒绝、进程已退出、窗口消失、PID 重用等竞态
/// 安全收敛为结构化结果。不触碰电源服务边界（唯一电源出口保持不变）。摘要脱敏、限长，
/// 不含命令行/窗口正文/无关路径。
/// </summary>
public sealed class CloseAppsService
{
    private const int MaxSummaryLength = 200;

    private static readonly HashSet<string> CriticalProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memory compression", "csrss", "winlogon",
            "services", "lsass", "smss", "wininit", "dwm", "fontdrvhost", "audiodg", "svchost"
        };

    private readonly IConfigurationService _configurationService;
    private readonly IProcessManager _processManager;
    private readonly IAppWindowManager _windowManager;

    public CloseAppsService(
        IConfigurationService configurationService,
        IProcessManager processManager,
        IAppWindowManager windowManager)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentNullException.ThrowIfNull(windowManager);
        _configurationService = configurationService;
        _processManager = processManager;
        _windowManager = windowManager;
    }

    public async Task<CloseAppsReport> CloseAllAsync(CancellationToken cancellationToken)
    {
        AppConfig config;
        try
        {
            var load = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (load.Status != ConfigurationLoadStatus.Success || load.Config is null)
            {
                return Failed("The configuration is unavailable; no applications were closed.");
            }

            config = load.Config;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failed("The configuration could not be loaded; no applications were closed.");
        }

        // 清单校验（fail-closed）：非法/缺失目标绝不静默回退为可能触发关闭/强杀的默认值。
        if (config.CloseApps is null)
        {
            return Failed("The CloseApps target list is invalid; no applications were closed.");
        }

        var errors = CloseAppsTargetList.Validate(config.CloseApps);
        if (errors.Count > 0)
        {
            return Failed("The CloseApps target list is invalid; no applications were closed.");
        }

        var defaultTimeout = TimeSpan.FromSeconds(config.CloseApps.GracefulTimeoutSeconds);
        var targets = CloseAppsTargetList.Normalize(config.CloseApps, defaultTimeout);

        if (targets.Count == 0)
        {
            return new CloseAppsReport { TargetCount = 0, Succeeded = true };
        }

        var results = new List<CloseAppTargetResult>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(CloseTarget(target, cancellationToken));
        }

        var succeeded = results.All(result => result.Succeeded);
        return new CloseAppsReport
        {
            TargetCount = targets.Count,
            Results = results,
            Succeeded = succeeded,
            Summary = succeeded ? string.Empty : BuildSummary(results)
        };
    }

    private CloseAppTargetResult CloseTarget(CloseAppTarget target, CancellationToken cancellationToken)
    {
        var candidates = Resolve(target);

        if (candidates.Count == 0)
        {
            return new CloseAppTargetResult
            {
                TargetId = target.TargetId,
                MatchedCount = 0,
                Status = CloseAppStatus.NotRunning,
                ForceKillAuthorized = target.ForceKillAllowed,
                Succeeded = true
            };
        }

        var statuses = new List<CloseAppStatus>(candidates.Count);
        foreach (var candidate in candidates)
        {
            statuses.Add(CloseCandidate(target, candidate, cancellationToken));
        }

        var succeeded = statuses.All(IsSuccess);
        return new CloseAppTargetResult
        {
            TargetId = target.TargetId,
            ProcessId = candidates.Count == 1 ? candidates[0].ProcessId : null,
            MatchedCount = candidates.Count,
            Status = Aggregate(statuses),
            ForceKillAuthorized = target.ForceKillAllowed,
            Succeeded = succeeded
        };
    }

    private List<ProcessSnapshot> Resolve(CloseAppTarget target)
    {
        if (target.ProcessId is int pid)
        {
            var snapshot = _processManager.GetProcessById(pid);
            return snapshot is null ? [] : [snapshot];
        }

        var path = target.ExecutablePath!;
        return _processManager.EnumerateProcesses()
            .Where(process => string.Equals(process.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private CloseAppStatus CloseCandidate(CloseAppTarget target, ProcessSnapshot resolved, CancellationToken cancellationToken)
    {
        // 重新读取当前身份，防御枚举与执行之间的 PID 退出/重用竞态。
        var current = _processManager.GetProcessById(resolved.ProcessId);
        if (current is null)
        {
            return CloseAppStatus.AlreadyExited;
        }

        if (resolved.StartTimeUtc != default
            && current.StartTimeUtc != default
            && current.StartTimeUtc != resolved.StartTimeUtc)
        {
            return CloseAppStatus.PidReuseDetected;
        }

        if (IsProtected(current))
        {
            return CloseAppStatus.SkippedProtected;
        }

        var exitStatus = _windowManager.GetExitStatus(current.ProcessId);
        if (exitStatus == ProcessExitStatus.Exited)
        {
            return CloseAppStatus.AlreadyExited;
        }

        if (exitStatus == ProcessExitStatus.Unknown)
        {
            // 退出状态无法确认：fail-closed，绝不当作已退出，也绝不强杀（D1-3）。
            return CloseAppStatus.ExitStatusUnknown;
        }

        // 优雅关闭优先。
        if (!_windowManager.HasMainWindow(current.ProcessId))
        {
            return target.ForceKillAllowed
                ? ForceKill(target, current, cancellationToken)
                : CloseAppStatus.NoWindow;
        }

        if (!_windowManager.RequestClose(current.ProcessId))
        {
            // 关闭请求未投递：可能窗口消失或进程已退出；复核后再决定。
            var recheck = _windowManager.GetExitStatus(current.ProcessId);
            if (recheck == ProcessExitStatus.Exited)
            {
                return CloseAppStatus.AlreadyExited;
            }

            if (recheck == ProcessExitStatus.Unknown)
            {
                return CloseAppStatus.ExitStatusUnknown;
            }

            return target.ForceKillAllowed
                ? ForceKill(target, current, cancellationToken)
                : CloseAppStatus.AccessDenied;
        }

        if (_windowManager.WaitForExit(current.ProcessId, target.GracefulTimeout, cancellationToken))
        {
            return CloseAppStatus.ClosedGracefully;
        }

        // 等待超时；强杀前由 ForceKill 再次复核取消（D1-1），绝不取消后强杀。
        return target.ForceKillAllowed
            ? ForceKill(target, current, cancellationToken)
            : CloseAppStatus.TimedOut;
    }

    private CloseAppStatus ForceKill(CloseAppTarget target, ProcessSnapshot current, CancellationToken cancellationToken)
    {
        // 强杀前再次复核取消：取消后绝不强杀（D1-1）。
        cancellationToken.ThrowIfCancellationRequested();

        var result = _windowManager.ForceKill(current.ProcessId, current.StartTimeUtc);
        return result.Status switch
        {
            ForceKillStatus.Killed => CloseAppStatus.ForceKilled,
            ForceKillStatus.AlreadyExited => CloseAppStatus.AlreadyExited,
            ForceKillStatus.PidReuseDetected => CloseAppStatus.PidReuseDetected,
            ForceKillStatus.ExitNotConfirmed => CloseAppStatus.ExitNotConfirmed,
            ForceKillStatus.AccessDenied => CloseAppStatus.AccessDenied,
            _ => CloseAppStatus.AccessDenied
        };
    }

    private bool IsProtected(ProcessSnapshot process)
    {
        // 自身进程：绝不关闭。
        if (process.ProcessId == _processManager.CurrentProcessId)
        {
            return true;
        }

        // 会话不明确（-1）或非当前用户会话：绝不关闭（fail-closed，D1-2）。
        // 仅当会话明确且等于当前会话才允许继续。
        if (process.SessionId != _processManager.CurrentSessionId)
        {
            return true;
        }

        // 系统关键进程（防御纵深；会话检查之外再按关键名收敛）。
        return CriticalProcessNames.Contains(process.ProcessName);
    }

    private static bool IsSuccess(CloseAppStatus status) => status switch
    {
        CloseAppStatus.ClosedGracefully or
        CloseAppStatus.AlreadyExited or
        CloseAppStatus.NotRunning or
        CloseAppStatus.ForceKilled => true,
        _ => false
    };

    private static CloseAppStatus Aggregate(IReadOnlyList<CloseAppStatus> statuses)
    {
        if (statuses.Contains(CloseAppStatus.SkippedProtected)) return CloseAppStatus.SkippedProtected;
        if (statuses.Contains(CloseAppStatus.PidReuseDetected)) return CloseAppStatus.PidReuseDetected;
        if (statuses.Contains(CloseAppStatus.ExitStatusUnknown)) return CloseAppStatus.ExitStatusUnknown;
        if (statuses.Contains(CloseAppStatus.AccessDenied)) return CloseAppStatus.AccessDenied;
        if (statuses.Contains(CloseAppStatus.NoWindow)) return CloseAppStatus.NoWindow;
        if (statuses.Contains(CloseAppStatus.TimedOut)) return CloseAppStatus.TimedOut;
        if (statuses.Contains(CloseAppStatus.ExitNotConfirmed)) return CloseAppStatus.ExitNotConfirmed;
        if (statuses.Contains(CloseAppStatus.ForceKilled)) return CloseAppStatus.ForceKilled;
        if (statuses.Contains(CloseAppStatus.ClosedGracefully)) return CloseAppStatus.ClosedGracefully;
        if (statuses.Contains(CloseAppStatus.AlreadyExited)) return CloseAppStatus.AlreadyExited;
        return CloseAppStatus.Unknown;
    }

    private static string BuildSummary(IReadOnlyList<CloseAppTargetResult> results)
    {
        var failed = results.Where(result => !result.Succeeded).Select(Describe);
        var summary = string.Join("; ", failed);
        return summary.Length <= MaxSummaryLength ? summary : summary[..MaxSummaryLength];
    }

    private static string Describe(CloseAppTargetResult result)
        => $"{result.TargetId}: {StatusLabel(result.Status)}";

    private static string StatusLabel(CloseAppStatus status) => status switch
    {
        CloseAppStatus.ClosedGracefully => "closed",
        CloseAppStatus.AlreadyExited => "already exited",
        CloseAppStatus.NotRunning => "not running",
        CloseAppStatus.NoWindow => "no window",
        CloseAppStatus.TimedOut => "timed out",
        CloseAppStatus.ForceKilled => "force-killed",
        CloseAppStatus.AccessDenied => "access denied",
        CloseAppStatus.SkippedProtected => "skipped (protected)",
        CloseAppStatus.PidReuseDetected => "pid reuse detected",
        CloseAppStatus.ExitNotConfirmed => "exit not confirmed",
        CloseAppStatus.ExitStatusUnknown => "exit status unknown",
        _ => "unknown"
    };

    private static CloseAppsReport Failed(string summary) => new()
    {
        TargetCount = 0,
        Succeeded = false,
        Summary = summary
    };
}
