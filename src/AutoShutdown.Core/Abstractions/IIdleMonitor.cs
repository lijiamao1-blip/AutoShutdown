namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 空闲监视器（基础设施/调度边界）：只提供输入活动事实（空闲时长），不做阈值判断。
/// 阈值判定由 IdleShutdownRule 负责；触发由 S13 调度与仲裁负责。
/// </summary>
public interface IIdleMonitor
{
    /// <summary>是否处于监控状态。</summary>
    bool IsMonitoring { get; }

    void Start();

    void Stop();

    /// <summary>当前空闲时长；null = 未监控或检测失败（默认不触发）。</summary>
    TimeSpan? GetIdleDuration();
}
