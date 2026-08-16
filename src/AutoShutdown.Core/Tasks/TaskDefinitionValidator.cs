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

    /// <summary>每月第 N 个工作日的合法 N 范围（任一自然月最多 23 个工作日）。</summary>
    public const int MinNthWorkday = 1;
    public const int MaxNthWorkday = 23;

    private static readonly HashSet<PowerAction> AllowedActions =
        [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate];

    /// <summary>是否为周期规则（执行后可重排下次触发）；一次性/倒计时规则触发后终结。</summary>
    public static bool IsRecurringKind(TaskKind kind)
        => kind is TaskKind.DailyAt or TaskKind.Weekdays or TaskKind.NthWorkdayOfMonth;

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

            case TaskKind.Weekdays:
                if (definition.TargetTimeOfDay is null)
                {
                    return $"{definition.Kind} tasks require TargetTimeOfDay.";
                }

                if (definition.Weekdays is null || definition.Weekdays.Count == 0)
                {
                    return $"{definition.Kind} tasks require a non-empty Weekdays collection.";
                }

                break;

            case TaskKind.NextWorkday:
                if (definition.TargetTimeOfDay is null)
                {
                    return $"{definition.Kind} tasks require TargetTimeOfDay.";
                }

                break;

            case TaskKind.NthWorkdayOfMonth:
                if (definition.TargetTimeOfDay is null)
                {
                    return $"{definition.Kind} tasks require TargetTimeOfDay.";
                }

                if (definition.NthWorkday is < MinNthWorkday or > MaxNthWorkday)
                {
                    return $"NthWorkday must be between {MinNthWorkday} and {MaxNthWorkday}; got {definition.NthWorkday}.";
                }

                break;

            case TaskKind.OneTime:
                if (definition.OneTimeDateTime is null)
                {
                    return $"{definition.Kind} tasks require OneTimeDateTime.";
                }

                break;

            default:
                return $"Task kind {definition.Kind} is not supported.";
        }

        return null;
    }
}
