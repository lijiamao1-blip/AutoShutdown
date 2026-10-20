using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Scheduling;

public sealed class SystemAsyncDeadline : IAsyncDeadline
{
    /// <summary>
    /// 单次等待上限。Windows 上 Task.Delay 基于不含睡眠/休眠的计时（unbiased interrupt time），
    /// 一次性长等待会在睡眠后整体推迟（睡 2 小时就晚 2 小时触发），也感知不到系统时间被调整。
    /// 分段等待并每段按墙钟重新核对，唤醒或改时后最多延迟一个分段即可纠正。
    /// </summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    private readonly IClock _clock;

    public SystemAsyncDeadline(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public async Task WaitUntilAsync(
        DateTimeOffset utcDeadline,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = utcDeadline - _clock.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var delay = remaining > MaxDelay ? MaxDelay : remaining;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
