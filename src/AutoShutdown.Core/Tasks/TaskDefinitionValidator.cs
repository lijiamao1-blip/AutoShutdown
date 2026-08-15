using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Tasks;

/// <summary>
/// 任务定义结构校验（单一事实来源）。TaskCollection 与 TaskService.Create 共用，
/// 避免两套结构规则长期互相漂移。此处不校验"执行时间是否仍在未来"——那依赖
/// now/timeZone，继续由 NextExecutionCalculator 在创建实例时判断。
/// </summary>
internal static class TaskDefinitionValidator
{
    public const int MinPriority = 0;
    public const int MaxPriority = 100;
    public const int MinWarningSeconds = 0;
    public const int MaxWarningSeconds = 86400;

    private static readonly HashSet<PowerAction> AllowedActions =
        [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate];

    /// <summary>返回结构错误描述；结构合法返回 null。</summary>
    public static string? GetStructuralError(TaskDefinition definition)
    {
        if (definition.Id == Guid.Empty)
        {
            return "definition.Id must not be an empty GUID.";
        }

        if (!AllowedActions.Contains(definition.Action))
        {
            return $"Action {definition.Action} is not allowed.";
        }

        if (definition.Priority is < MinPriority or > MaxPriority)
        {
            return $"Priority must be between {MinPriority} and {MaxPriority}; got {definition.Priority}.";
        }

        if (definition.WarningSeconds is < MinWarningSeconds or > MaxWarningSeconds)
        {
            return $"WarningSeconds must be between {MinWarningSeconds} and {MaxWarningSeconds}; got {definition.WarningSeconds}.";
        }

        switch (definition.Kind)
        {
            case TaskKind.Countdown:
                if (definition.CountdownDuration is null)
                {
                    return "Countdown tasks require CountdownDuration.";
                }

                if (definition.CountdownDuration.Value <= TimeSpan.Zero)
                {
                    return "CountdownDuration must be strictly positive.";
                }

                break;

            case TaskKind.TodayAt:
            case TaskKind.DailyAt:
                if (definition.TargetTimeOfDay is null)
                {
                    return $"{definition.Kind} tasks require TargetTimeOfDay.";
                }

                break;

            default:
                return $"Task kind {definition.Kind} is not supported.";
        }

        return null;
    }
}
