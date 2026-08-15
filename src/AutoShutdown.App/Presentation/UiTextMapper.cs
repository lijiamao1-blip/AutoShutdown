using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.App.Presentation;

public static class UiTextMapper
{
    public static string Map(PowerAction action) => action switch
    {
        PowerAction.Shutdown => "关机",
        PowerAction.Restart => "重启",
        PowerAction.Sleep => "睡眠",
        PowerAction.Hibernate => "休眠",
        _ => "未知状态"
    };

    public static string Map(TaskState state) => state switch
    {
        TaskState.Idle => "空闲",
        TaskState.Scheduled => "等待执行",
        TaskState.Warning => "提醒中",
        TaskState.Executing => "正在执行",
        TaskState.Cancelled => "已取消",
        TaskState.Completed => "已完成",
        TaskState.Failed => "执行失败",
        TaskState.Interrupted => "恢复后已中断",
        _ => "状态未知"
    };

    public static string MapEngine(SchedulerEngineStatus status) => status switch
    {
        SchedulerEngineStatus.Created => "启动中",
        SchedulerEngineStatus.Running => "运行正常",
        SchedulerEngineStatus.Faulted => "故障",
        SchedulerEngineStatus.Stopped => "已停止",
        _ => "未知状态"
    };

    public static string MapCommand(SchedulerCommandStatus status) => status switch
    {
        SchedulerCommandStatus.Success => "成功",
        SchedulerCommandStatus.NotRunning => "调度服务未运行",
        SchedulerCommandStatus.Faulted => "调度服务故障",
        SchedulerCommandStatus.NoCurrentTask => "当前没有任务",
        SchedulerCommandStatus.ActiveTaskExists => "已有活动任务",
        SchedulerCommandStatus.StaleCommand => "任务已变化，请刷新后重试",
        SchedulerCommandStatus.TaskServiceRejected => "任务服务拒绝",
        SchedulerCommandStatus.PersistenceFailed => "状态保存失败",
        SchedulerCommandStatus.InvalidCommand => "无效命令",
        SchedulerCommandStatus.TransitionRejected => "状态不允许该操作",
        _ => "未知状态"
    };

    public static string MapTaskCommand(TaskCommandStatus status) => status switch
    {
        TaskCommandStatus.Success => "成功",
        TaskCommandStatus.InvalidDefinition => "任务定义无效",
        TaskCommandStatus.ScheduleCalculationFailed => "时间计算失败",
        TaskCommandStatus.InvalidCurrentInstance => "实例标识无效",
        TaskCommandStatus.InvalidDuration => "时长无效",
        TaskCommandStatus.TransitionRejected => "状态不允许该操作",
        TaskCommandStatus.AlreadyExecuted => "任务已执行",
        _ => "未知状态"
    };

    public static string MapConfig(ConfigurationLoadStatus status) => status switch
    {
        ConfigurationLoadStatus.Success => "安全有效",
        ConfigurationLoadStatus.Missing => "配置文件缺失",
        ConfigurationLoadStatus.Corrupt => "配置文件损坏",
        ConfigurationLoadStatus.IoFailure => "配置文件读取失败",
        ConfigurationLoadStatus.Invalid => "配置内容无效",
        ConfigurationLoadStatus.UnsupportedVersion => "配置版本不受支持",
        ConfigurationLoadStatus.MigrationUnavailable => "配置迁移不可用",
        _ => "未知状态"
    };
}
