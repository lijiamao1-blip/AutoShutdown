using AutoShutdown.Core.State;

namespace AutoShutdown.Core.WakeOnLan;

/// <summary>
/// WoL 任务执行器（S21）。由调度器在 Executing 态经
/// <c>ShutdownScheduledTaskHandler</c> 按 <see cref="PowerAction.WakeOnLan"/> 派发；
/// 失败抛 <c>ScheduledTaskHandlingException</c> 使实例进入 Faulted（绝不伪造成功）。
/// WoL 任务不经过双闸门/真实电源，只向用户显式配置的局域网目标发送。
/// </summary>
public interface IWakeOnLanTaskExecutor
{
    Task ExecuteAsync(TaskInstance instance, CancellationToken cancellationToken);
}
