using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Scheduling;

/// <summary>
/// 过渡仲裁实现（T04 占位；正式实现归 T06）。
/// 单个到期 → 该实例即为赢家；多个到期 → 无法自动决策，要求用户决策。
/// 引擎对「多个到期且无自动赢家」采取安全失败（fault），不擅自二选一。
/// </summary>
public sealed class NoOpTaskArbitrator : ITaskArbitrator
{
    public TaskArbitrationResult Arbitrate(
        IReadOnlyList<TaskInstance> dueInstances,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dueInstances);

        if (dueInstances.Count == 1)
        {
            return new TaskArbitrationResult
            {
                WinnerTaskId = dueInstances[0].SourceTaskId,
                DecisionReason = "A single task is due; it wins by default."
            };
        }

        return new TaskArbitrationResult
        {
            RequiresUserDecision = true,
            DecisionReason =
                $"{dueInstances.Count} tasks are due simultaneously and no arbitrator is configured."
        };
    }
}
