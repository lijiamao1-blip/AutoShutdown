using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Scheduling;

public sealed class NullScheduledTaskHandler : IScheduledTaskHandler
{
    public Task HandleDueAsync(
        TaskInstance instance,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
