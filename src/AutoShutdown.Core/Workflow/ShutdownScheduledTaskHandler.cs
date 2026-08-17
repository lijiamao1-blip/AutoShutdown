using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.WakeOnLan;

namespace AutoShutdown.Core.Workflow;

public sealed class ShutdownScheduledTaskHandler : IScheduledTaskHandler
{
    private readonly IShutdownWorkflow _workflow;
    private readonly IWakeOnLanTaskExecutor? _wakeOnLanExecutor;

    public ShutdownScheduledTaskHandler(
        IShutdownWorkflow workflow,
        IWakeOnLanTaskExecutor? wakeOnLanExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
        _wakeOnLanExecutor = wakeOnLanExecutor;
    }

    public async Task HandleDueAsync(
        TaskInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // S21：WoL 是显式的调度任务类型，经调度器完整状态机触发（不经双闸门/真实电源）。
        // 只向用户显式配置的局域网目标发送；失败抛 ScheduledTaskHandlingException → 实例 Faulted。
        if (instance.ActionSnapshot == PowerAction.WakeOnLan)
        {
            if (_wakeOnLanExecutor is null)
            {
                throw new ScheduledTaskHandlingException(
                    "WakeOnLan execution is not configured.");
            }

            await _wakeOnLanExecutor
                .ExecuteAsync(instance, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var result = await _workflow.ExecuteAsync(instance, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new ScheduledTaskHandlingException(result);
        }
    }
}
