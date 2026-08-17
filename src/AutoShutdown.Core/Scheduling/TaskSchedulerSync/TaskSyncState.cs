namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

/// <summary>
/// 外部任务当前可观察状态（由适配器从系统查询得到）。与 <see cref="ExternalTaskSpec"/>
/// 的字段一一对应，供幂等比较判定「是否需要创建/更新」。
/// </summary>
public sealed record ExternalTaskState
{
    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public string TriggerSignature { get; init; } = string.Empty;

    public string ActionPath { get; init; } = string.Empty;

    public string Arguments { get; init; } = string.Empty;

    public DateTime? LastRunTime { get; init; }
}

/// <summary>
/// 幂等比较：期望规格 vs 可观察状态是否等价。比较 name（稳定）、enabled、触发签名与
/// 动作（路径+参数）。描述（Description）仅为审计信息，不参与等价判定。
/// </summary>
public static class TaskSyncEquivalence
{
    public static bool IsEquivalent(ExternalTaskSpec desired, ExternalTaskState observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        return string.Equals(desired.Name, observed.Name, StringComparison.Ordinal)
            && desired.Enabled == observed.Enabled
            && string.Equals(
                ExternalTriggerSignature.Build(desired.Trigger),
                observed.TriggerSignature,
                StringComparison.Ordinal)
            && string.Equals(desired.AppExePath, observed.ActionPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(desired.TriggerArgument, observed.Arguments, StringComparison.Ordinal);
    }
}
