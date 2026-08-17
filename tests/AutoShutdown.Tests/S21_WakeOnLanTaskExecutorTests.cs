using AutoShutdown.Core.State;
using AutoShutdown.Core.WakeOnLan;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C3：WoL 任务执行器。经调度 handler 派发后向用户显式配置的目标发送；目标缺失、
/// 发送失败一律抛 ScheduledTaskHandlingException（调度器据此标记实例 Faulted，绝不伪造成功）。
/// 全部使用替身服务，自动化测试绝不发送真实 UDP。
/// </summary>
public sealed class S21_WakeOnLanTaskExecutorTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TargetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Execute_WhenSendSucceeds_ReturnsWithoutException()
    {
        var service = new FakeWolService(WolSendResult.Success("sent"));
        var executor = new WakeOnLanTaskExecutor(service);

        await executor.ExecuteAsync(WolInstance(targetId: TargetId), CancellationToken.None);

        Assert.Equal(TargetId, service.LastTargetId);
    }

    [Fact]
    public async Task Execute_WhenSendFails_ThrowsScheduledTaskHandlingException()
    {
        var service = new FakeWolService(
            WolSendResult.Failure(WolSendStatus.SendFailed, "socket refused"));
        var executor = new WakeOnLanTaskExecutor(service);

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => executor.ExecuteAsync(WolInstance(targetId: TargetId), CancellationToken.None));

        Assert.Contains("socket refused", exception.Message);
        Assert.Null(exception.Result);
    }

    [Fact]
    public async Task Execute_WhenTargetNotFound_ThrowsScheduledTaskHandlingException()
    {
        var service = new FakeWolService(
            WolSendResult.Failure(WolSendStatus.TargetNotFound, "no such target"));
        var executor = new WakeOnLanTaskExecutor(service);

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => executor.ExecuteAsync(WolInstance(targetId: TargetId), CancellationToken.None));

        Assert.Contains("no such target", exception.Message);
    }

    [Fact]
    public async Task Execute_WhenNoTargetMachineId_ThrowsScheduledTaskHandlingExceptionWithoutSending()
    {
        var service = new FakeWolService(WolSendResult.Success("sent"));
        var executor = new WakeOnLanTaskExecutor(service);

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => executor.ExecuteAsync(WolInstance(targetId: null), CancellationToken.None));

        Assert.Contains("no valid target machine id", exception.Message);
        Assert.Equal(0, service.SendCalls);
    }

    [Fact]
    public async Task Execute_WhenEmptyTargetMachineId_ThrowsScheduledTaskHandlingException()
    {
        var service = new FakeWolService(WolSendResult.Success("sent"));
        var executor = new WakeOnLanTaskExecutor(service);

        var exception = await Assert.ThrowsAsync<ScheduledTaskHandlingException>(
            () => executor.ExecuteAsync(WolInstance(targetId: Guid.Empty), CancellationToken.None));

        Assert.Contains("no valid target machine id", exception.Message);
        Assert.Equal(0, service.SendCalls);
    }

    [Fact]
    public async Task Execute_WhenActionIsNotWakeOnLan_ThrowsInvalidOperation()
    {
        var service = new FakeWolService(WolSendResult.Success("sent"));
        var executor = new WakeOnLanTaskExecutor(service);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(
                WolInstance(targetId: TargetId) with { ActionSnapshot = PowerAction.Shutdown },
                CancellationToken.None));

        Assert.Equal(0, service.SendCalls);
    }

    private static TaskInstance WolInstance(Guid? targetId) => new()
    {
        InstanceId = InstanceId,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.WakeOnLan,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = StageToken,
        HasExecuted = true,
        TargetMachineId = targetId,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private sealed class FakeWolService : IWakeOnLanService
    {
        private readonly WolSendResult _result;

        public FakeWolService(WolSendResult result) => _result = result;

        public int SendCalls { get; private set; }

        public Guid? LastTargetId { get; private set; }

        public Task<WolSendResult> SendAsync(
            Guid targetMachineId,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            LastTargetId = targetMachineId;
            return Task.FromResult(_result);
        }
    }
}
