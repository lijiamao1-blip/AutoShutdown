using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Scheduling;

public sealed class SystemAsyncDeadline : IAsyncDeadline
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(int.MaxValue);

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
