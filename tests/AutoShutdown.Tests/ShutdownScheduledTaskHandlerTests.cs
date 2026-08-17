using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class ShutdownScheduledTaskHandlerTests
{
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact(Timeout = 2000)]
    public async Task HandleDueAsync_WhenWorkflowSucceeds_ReturnsNormally()
    {
        var workflow = new FixedWorkflow(new ShutdownWorkflowResult
        {
            Status = ShutdownWorkflowStatus.Simulated,
            DecisionCode = ShutdownDecisionCode.Allowed,
            Message = "ok"
        });
        var handler = new ShutdownScheduledTaskHandler(workflow);

        await handler.HandleDueAsync(ValidInstance(), CancellationToken.None);
    }

    [Fact(Timeout = 2000)]
    public async Task HandleDueAsync_WhenWorkflowRejects_ThrowsExceptionWithSameResult()
    {
        var rejected = new ShutdownWorkflowResult
        {
            Status = ShutdownWorkflowStatus.Rejected,
            DecisionCode = ShutdownDecisionCode.ConfigurationUnavailable,
            Message = "no config"
        };
        var handler = new ShutdownScheduledTaskHandler(new FixedWorkflow(rejected));

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => handler.HandleDueAsync(ValidInstance(), CancellationToken.None));

        Assert.Equal(rejected, exception.Result);
    }

    [Fact]
    public void ScheduledTaskHandlingException_WhenResultIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ScheduledTaskHandlingException((ShutdownWorkflowResult)null!));
    }

    private static TaskInstance ValidInstance() => new()
    {
        InstanceId = InstanceId1,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = StageToken1,
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private sealed class FixedWorkflow : IShutdownWorkflow
    {
        private readonly ShutdownWorkflowResult _result;

        public FixedWorkflow(ShutdownWorkflowResult result) => _result = result;

        public Task<ShutdownWorkflowResult> ExecuteAsync(
            TaskInstance instance,
            CancellationToken cancellationToken) => Task.FromResult(_result);
    }
}
