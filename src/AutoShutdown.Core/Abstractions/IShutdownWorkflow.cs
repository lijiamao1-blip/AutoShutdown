using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;

namespace AutoShutdown.Core.Abstractions;

public interface IShutdownWorkflow
{
    Task<ShutdownWorkflowResult> ExecuteAsync(
        TaskInstance instance,
        CancellationToken cancellationToken);
}
