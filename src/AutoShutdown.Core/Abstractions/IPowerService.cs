using AutoShutdown.Core.Power;

namespace AutoShutdown.Core.Abstractions;

public interface IPowerService
{
    Task<PowerResult> ExecuteAsync(
        PowerRequest request,
        CancellationToken cancellationToken);
}
