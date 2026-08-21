using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;
using AutoShutdown.Core.Storage;

namespace AutoShutdown.App.AppHost;

/// <summary>
/// 任务计划程序同步开关的启动装载（S-STARTUP-D1）。把「读取 task-sync.json 决定是否启用
/// 同步」从 DI 构造阶段（无界同步文件 I/O，会把 UI 线程卡死在互斥体获取与调度引擎启动之间）
/// 移到有界、可记录、fail-closed 的启动步骤：线程池上读取（不在 UI 线程同步文件 I/O），
/// <see cref="Task.WaitAsync(TimeSpan)"/> 限时；读取失败/超时一律保持同步关闭（绝不静默启用，
/// 启用会创建外部计划任务）。
/// </summary>
internal static class StartupTaskSyncSettingsGate
{
    public static bool LoadEnabled(
        TaskSyncSettingsStore store,
        IApplicationLogger logger,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var load = Task.Run(() => store.LoadAsync(CancellationToken.None))
                .WaitAsync(timeout)
                .GetAwaiter()
                .GetResult();

            var enabled = load.Status == TaskSyncSettingsLoadStatus.Success
                && load.Document?.Enabled == true;

            logger.Info(
                "TaskSyncSettingsLoaded",
                "任务计划程序同步开关已加载：" + (enabled ? "启用" : "关闭")
                + "（" + load.Status + "）。");
            return enabled;
        }
        catch (Exception exception)
        {
            // fail-closed：缺失/损坏/超时一律保持同步关闭，绝不静默启用（启用会创建外部任务）。
            logger.Warning(
                "TaskSyncSettingsLoadFailed",
                "任务计划程序同步开关读取失败（" + exception.Message + "），保持关闭。");
            return false;
        }
    }
}
