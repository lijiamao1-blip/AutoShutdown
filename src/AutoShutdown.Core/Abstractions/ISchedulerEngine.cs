using AutoShutdown.Core.Scheduling;

namespace AutoShutdown.Core.Abstractions;

public interface ISchedulerEngine
{
    Task RunAsync(CancellationToken cancellationToken);

    Task<SchedulerCommandResult> SubmitAsync(
        SchedulerCommand command,
        CancellationToken cancellationToken);

    SchedulerSnapshot GetSnapshot();
}
