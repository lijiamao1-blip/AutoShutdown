using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Power;

public sealed class FakePowerService : IPowerService
{
    private readonly object _syncRoot = new();
    private readonly List<PowerRequest> _invocations = [];

    public IReadOnlyList<PowerRequest> Invocations
    {
        get
        {
            lock (_syncRoot)
            {
                return _invocations.ToArray();
            }
        }
    }

    public Task<PowerResult> ExecuteAsync(
        PowerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            _invocations.Add(request);
        }

        return Task.FromResult(new PowerResult
        {
            Outcome = PowerOutcome.Simulated,
            WasSimulated = true,
            Message = "Power action was recorded by the fake service."
        });
    }
}
