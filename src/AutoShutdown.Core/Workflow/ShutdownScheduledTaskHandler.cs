using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Workflow;

public sealed class ShutdownScheduledTaskHandler : IScheduledTaskHandler
{
    private readonly IShutdownWorkflow _workflow;

    public ShutdownScheduledTaskHandler(IShutdownWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
    }

    public async Task HandleDueAsync(
        TaskInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var result = await _workflow.ExecuteAsync(instance, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new ScheduledTaskHandlingException(result);
        }
    }
}
