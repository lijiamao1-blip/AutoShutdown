using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class FakePowerServiceTests
{
    [Fact]
    public async Task ExecuteAsync_RecordsRequestAndReturnsSimulatedResult()
    {
        var service = new FakePowerService();
        var request = new PowerRequest
        {
            Action = PowerAction.Shutdown,
            InstanceId = Guid.NewGuid(),
            Reason = "S1 verification"
        };

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.WasSimulated);
        Assert.Equal(PowerOutcome.Simulated, result.Outcome);
        Assert.Single(service.Invocations);
        Assert.Same(request, service.Invocations[0]);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancelledToken_DoesNotRecordRequest()
    {
        var service = new FakePowerService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ExecuteAsync(new PowerRequest(), cancellation.Token));

        Assert.Empty(service.Invocations);
    }

    [Fact]
    public async Task Invocations_IsAnImmutableSnapshot()
    {
        var service = new FakePowerService();
        await service.ExecuteAsync(new PowerRequest(), CancellationToken.None);
        var snapshot = service.Invocations;

        await service.ExecuteAsync(new PowerRequest(), CancellationToken.None);

        Assert.Single(snapshot);
        Assert.Equal(2, service.Invocations.Count);
    }
}
