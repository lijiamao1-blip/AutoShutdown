using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;

namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// WoL 任务执行器默认实现（S21）。校验实例身份后向目标机器发送 Magic Packet；
/// 发送失败抛 <see cref="ScheduledTaskHandlingException"/>（调度器据此标记实例 Faulted）。
/// </summary>
public sealed class WakeOnLanTaskExecutor : IWakeOnLanTaskExecutor
{
    private readonly IWakeOnLanService _wolService;

    public WakeOnLanTaskExecutor(IWakeOnLanService wolService)
    {
        ArgumentNullException.ThrowIfNull(wolService);
        _wolService = wolService;
    }

    public async Task ExecuteAsync(
        TaskInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.ActionSnapshot != PowerAction.WakeOnLan)
        {
            throw new InvalidOperationException("The task instance action is not WakeOnLan.");
        }

        if (instance.TargetMachineId is not { } targetId || targetId == Guid.Empty)
        {
            throw new ScheduledTaskHandlingException(
                "The WakeOnLan task instance has no valid target machine id.");
        }

        var send = await _wolService
            .SendAsync(targetId, cancellationToken)
            .ConfigureAwait(false);

        if (!send.Succeeded)
        {
            throw new ScheduledTaskHandlingException(
                $"The WakeOnLan task failed: {send.Message}");
        }
    }
}
