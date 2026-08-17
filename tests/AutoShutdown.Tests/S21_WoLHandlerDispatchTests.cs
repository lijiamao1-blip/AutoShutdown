using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.WakeOnLan;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C3：调度 handler 派发。ActionSnapshot==WakeOnLan 时走 WoL 执行器（不经工作流/双闸门）；
/// 其它电源动作走 ShutdownWorkflow；WoL 失败以 ScheduledTaskHandlingException 传播（实例 Faulted）。
/// </summary>
public sealed class S21_WoLHandlerDispatchTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TargetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Handle_WhenWakeOnLanAction_DispatchesToExecutorNotWorkflow()
    {
        var workflow = new FakeWorkflow();
        var executor = new FakeExecutor();
        var handler = new ShutdownScheduledTaskHandler(workflow, executor);

        await handler.HandleDueAsync(WolInstance(), CancellationToken.None);

        Assert.Equal(1, executor.Calls);
        Assert.Equal(0, workflow.Calls);
    }

    [Fact]
    public async Task Handle_WhenWakeOnLanExecutorThrows_PropagatesHandlerException()
    {
        var workflow = new FakeWorkflow();
        var executor = new FakeExecutor
        {
            ThrowHandlerException = new ScheduledTaskHandlingException("wol send failed")
        };
        var handler = new ShutdownScheduledTaskHandler(workflow, executor);

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => handler.HandleDueAsync(WolInstance(), CancellationToken.None));

        Assert.Contains("wol send failed", exception.Message);
        Assert.Equal(0, workflow.Calls);
    }

    [Fact]
    public async Task Handle_WhenWakeOnLanButExecutorNotConfigured_ThrowsHandlerException()
    {
        var workflow = new FakeWorkflow();
        var handler = new ShutdownScheduledTaskHandler(workflow); // executor null

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => handler.HandleDueAsync(WolInstance(), CancellationToken.None));

        Assert.Contains("not configured", exception.Message);
        Assert.Equal(0, workflow.Calls);
    }

    [Fact]
    public async Task Handle_WhenPowerAction_DispatchesToWorkflowNotExecutor()
    {
        var workflow = new FakeWorkflow();
        var executor = new FakeExecutor();
        var handler = new ShutdownScheduledTaskHandler(workflow, executor);

        await handler.HandleDueAsync(
            WolInstance() with { ActionSnapshot = PowerAction.Shutdown, TargetMachineId = null },
            CancellationToken.None);

        Assert.Equal(1, workflow.Calls);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Handle_WhenPowerActionFails_ThrowsHandlerExceptionWithResult()
    {
        var workflow = new FakeWorkflow
        {
            Result = new ShutdownWorkflowResult
            {
                Status = ShutdownWorkflowStatus.Rejected,
                Message = "rejected by double gate"
            }
        };
        var handler = new ShutdownScheduledTaskHandler(workflow);

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => handler.HandleDueAsync(
                WolInstance() with { ActionSnapshot = PowerAction.Shutdown, TargetMachineId = null },
                CancellationToken.None));

        Assert.Equal(ShutdownWorkflowStatus.Rejected, exception.Result!.Status);
    }

    private static TaskInstance WolInstance() => new()
    {
        InstanceId = InstanceId,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.WakeOnLan,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = StageToken,
        HasExecuted = true,
        TargetMachineId = TargetId,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private sealed class FakeWorkflow : IShutdownWorkflow
    {
        public ShutdownWorkflowResult Result { get; set; } = new()
        {
            Status = ShutdownWorkflowStatus.Accepted,
            Message = "accepted"
        };

        public int Calls { get; private set; }

        public Task<ShutdownWorkflowResult> ExecuteAsync(
            TaskInstance instance,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeExecutor : IWakeOnLanTaskExecutor
    {
        public ScheduledTaskHandlingException? ThrowHandlerException { get; init; }

        public int Calls { get; private set; }

        public Task ExecuteAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            Calls++;
            if (ThrowHandlerException is not null)
            {
                throw ThrowHandlerException;
            }

            return Task.CompletedTask;
        }
    }
}
