using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Scheduling;

/// <summary>
/// S13-T06 同时到期仲裁（仅决策，不执行电源）。实现 <see cref="ITaskArbitrator"/>：
/// 相同动作合并为单个获胜执行意图；不同动作先按 Priority（大者胜）再按 GATE-A1 强度
/// Shutdown &gt; Restart &gt; Hibernate &gt; Sleep 决出赢家；落选任务进入
/// <c>RescheduledTaskIds</c>（改期 ≥ <see cref="MinimumRescheduleDelay"/>，由消费方引擎落实）；
/// 强制（不可被仲裁覆盖）任务冲突返回 <c>RequiresUserDecision=true</c>。
/// 纯逻辑：不接触 <see cref="IPowerService"/>、不写日志、不写状态。
/// </summary>
public sealed class TaskArbitrator : ITaskArbitrator
{
    /// <summary>落选任务的最小改期延迟（架构 S13 §5.5「至少 5 分钟后」）。</summary>
    public static readonly TimeSpan MinimumRescheduleDelay = TimeSpan.FromMinutes(5);

    private readonly ITaskService _taskService;

    /// <summary>
    /// 被用户配置为「不可被仲裁覆盖」的任务 id 集合。该标志尚未在冻结任务定义模型
    /// 中承载（UI 需求包 §8「人工冲突响应契约未冻结」），故作为独立输入注入；
    /// 生产暂传空集，待 T06/T08 接口闭合后改为从任务定义读取。
    /// </summary>
    private readonly IReadOnlySet<Guid> _forcedTaskIds;

    public TaskArbitrator(ITaskService taskService, IReadOnlySet<Guid>? forcedTaskIds = null)
    {
        ArgumentNullException.ThrowIfNull(taskService);
        _taskService = taskService;
        _forcedTaskIds = forcedTaskIds ?? new HashSet<Guid>();
    }

    public TaskArbitrationResult Arbitrate(
        IReadOnlyList<TaskInstance> dueInstances,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dueInstances);

        if (dueInstances.Count == 0)
        {
            return new TaskArbitrationResult
            {
                DecisionReason = "No due instances to arbitrate."
            };
        }

        if (dueInstances.Count == 1)
        {
            return new TaskArbitrationResult
            {
                WinnerTaskId = dueInstances[0].SourceTaskId,
                DecisionReason = "A single task is due; it wins by default."
            };
        }

        var forced = dueInstances
            .Where(instance => _forcedTaskIds.Contains(instance.SourceTaskId))
            .Select(instance => instance.SourceTaskId)
            .ToList();
        if (forced.Count > 0)
        {
            return new TaskArbitrationResult
            {
                RequiresUserDecision = true,
                DecisionReason =
                    "Forced task(s) conflict and cannot be overridden by arbitration: "
                    + string.Join(", ", forced) + ". User decision required."
            };
        }

        if (dueInstances.Select(instance => instance.ActionSnapshot).Distinct().Count() == 1)
        {
            // 同动作合并：所有到期任务动作相同，合并为一次电源操作意图。
            var winner = dueInstances
                .OrderByDescending(instance => PriorityOf(instance))
                .ThenBy(instance => instance.SourceTaskId)
                .First();
            var merged = dueInstances
                .Where(instance => instance.SourceTaskId != winner.SourceTaskId)
                .Select(instance => instance.SourceTaskId)
                .ToList();

            return new TaskArbitrationResult
            {
                WinnerTaskId = winner.SourceTaskId,
                MergedTaskIds = merged,
                DecisionReason = "Same action merged into a single execution intent."
            };
        }

        // 不同动作：Priority 降序，同优先级按 GATE-A1 强度降序，再按 task id 稳定排序。
        var ordered = dueInstances
            .OrderByDescending(instance => PriorityOf(instance))
            .ThenByDescending(instance => StrengthOf(instance.ActionSnapshot))
            .ThenBy(instance => instance.SourceTaskId)
            .ToList();

        var winnerId = ordered[0].SourceTaskId;
        var rescheduled = ordered
            .Skip(1)
            .Select(instance => instance.SourceTaskId)
            .ToList();

        return new TaskArbitrationResult
        {
            WinnerTaskId = winnerId,
            RescheduledTaskIds = rescheduled,
            DecisionReason =
                "Different actions: winner by priority then strength (Shutdown > Restart > Hibernate > Sleep); "
                + "losers rescheduled by at least " + MinimumRescheduleDelay.TotalMinutes + " minute(s)."
        };
    }

    private int PriorityOf(TaskInstance instance)
        => _taskService.Get(instance.SourceTaskId)?.Priority ?? 0;

    private static int StrengthOf(PowerAction action) => action switch
    {
        PowerAction.Shutdown => 4,
        PowerAction.Restart => 3,
        PowerAction.Hibernate => 2,
        PowerAction.Sleep => 1,
        _ => 0
    };
}
