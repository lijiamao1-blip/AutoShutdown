using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Abstractions;

public interface ITaskService
{
    TaskCommandResult Create(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone);

    TaskCommandResult Snooze(
        TaskInstance current,
        TimeSpan duration,
        DateTimeOffset now);

    TaskCommandResult Cancel(TaskInstance current);

    /// <summary>
    /// 仲裁落选改期（S13-T09）：将 Waiting 实例字段级重排到 now+delay（须 ≥5 分钟，
    /// 与 <see cref="AutoShutdown.Core.Scheduling.TaskArbitrator.MinimumRescheduleDelay"/> 一致），
    /// 刷新 StageToken 并清除告警窗口；仅 Waiting 允许（冻结白名单无回边）。
    /// </summary>
    TaskCommandResult RescheduleAfterArbitration(
        TaskInstance current,
        TimeSpan delay,
        DateTimeOffset now);

    TaskCommandResult RescheduleDaily(
        TaskDefinition definition,
        TaskInstance current,
        DateTimeOffset now,
        TimeZoneInfo timeZone);

    // ===== 任务定义领域模型集合化 CRUD（S13-T02A，内存领域模型，无持久化） =====

    /// <summary>新增任务定义（Id 已存在则拒绝）。</summary>
    TaskCollectionResult Add(TaskDefinition definition);

    /// <summary>更新已存在的任务定义（Id 必须存在且不变）。</summary>
    TaskCollectionResult Update(TaskDefinition definition);

    /// <summary>移除任务定义（Id 不存在则拒绝）。</summary>
    TaskCollectionResult Remove(Guid taskId);

    /// <summary>分别启用/禁用任务（Id 不存在则拒绝）。</summary>
    TaskCollectionResult SetEnabled(Guid taskId, bool isEnabled);

    /// <summary>按 Id 获取任务定义；不存在返回 null。</summary>
    TaskDefinition? Get(Guid taskId);

    /// <summary>获取当前全部任务定义的快照。</summary>
    IReadOnlyCollection<TaskDefinition> GetAll();
}
