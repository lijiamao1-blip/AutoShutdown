using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Idle;

/// <summary>
/// 空闲监视器：包装 <see cref="IIdleInputSource"/>，提供输入活动事实（空闲时长）。
/// 只陈述「空闲了多久」，不决定阈值、不触发电源。启停由调度循环控制。
/// </summary>
public sealed class IdleMonitor : IIdleMonitor
{
    private readonly IIdleInputSource _source;

    public IdleMonitor(IIdleInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public bool IsMonitoring { get; private set; }

    public void Start() => IsMonitoring = true;

    public void Stop() => IsMonitoring = false;

    public TimeSpan? GetIdleDuration()
        => IsMonitoring ? _source.GetIdleDuration() : null;
}
