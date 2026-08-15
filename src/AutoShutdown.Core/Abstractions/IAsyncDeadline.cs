namespace AutoShutdown.Core.Abstractions;

public interface IAsyncDeadline
{
    Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken);
}
