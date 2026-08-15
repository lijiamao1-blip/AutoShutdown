using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Abstractions;

public interface IScheduledTaskHandler
{
    Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken);
}
