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

    /// <summary>空闲触发阈值的合法秒数范围（1 秒 ~ 7 天）。</summary>
    public const int MinIdleThresholdSeconds = 1;
    public const int MaxIdleThresholdSeconds = 604800;

    private static readonly HashSet<PowerAction> AllowedActions =
        [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate, PowerAction.WakeOnLan];

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

        // S21：WoL 任务必须携带非空 TargetMachineId，且禁止携带 RTC 唤醒时间
        //（RTC 唤醒只作为受控关机前步骤，从不独立触发，也绝不与 WoL 混用）。
        // 电源动作则禁止携带 TargetMachineId（WoL 专属字段，fail-closed）。
        if (definition.Action == PowerAction.WakeOnLan)
        {
            if (definition.TargetMachineId is not { } targetId || targetId == Guid.Empty)
            {
                return "WakeOnLan tasks require a non-empty TargetMachineId.";
            }

            if (definition.RtcWakeTimeUtc is not null)
            {
                return "WakeOnLan tasks must not set RtcWakeTimeUtc.";
            }
        }
        else if (definition.TargetMachineId is not null)
        {
            return "TargetMachineId is only valid for WakeOnLan tasks.";
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

            case TaskKind.Idle:
                // 阈值可空（继承全局默认）；一旦指定必须为正且不超过上限（否则默认不触发）。
                if (definition.IdleThresholdSeconds is { } idleThreshold
                    && idleThreshold is < MinIdleThresholdSeconds or > MaxIdleThresholdSeconds)
                {
                    return $"IdleThresholdSeconds must be between {MinIdleThresholdSeconds} and {MaxIdleThresholdSeconds} when specified; got {idleThreshold}.";
                }

                break;

            default:
                return $"Task kind {definition.Kind} is not supported.";
        }

        return null;
    }
}
