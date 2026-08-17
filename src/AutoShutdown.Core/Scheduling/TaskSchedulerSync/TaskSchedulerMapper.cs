using System.Globalization;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

/// <summary>
/// 纯映射：本地 <see cref="TaskDefinition"/>（唯一事实源）→ 平台无关的外部任务规格
/// （<see cref="ExternalTaskSpec"/>）。冻结字段映射、触发形态、禁用状态、时区与稳定命名；
/// 不触碰系统，不读取外部状态，可全量单测。动作永远是「回调本地应用」——外部任务绝不
/// 直接携带电源命令。
/// </summary>
public sealed class TaskSchedulerMapper
{
    /// <summary>外部任务动作参数前缀：只回调本地应用，由本地唯一调度/Workflow 执行。</summary>
    public const string TriggerArgumentPrefix = "--trigger-task";

    private readonly INextExecutionCalculator _calculator;

    public TaskSchedulerMapper(INextExecutionCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        _calculator = calculator;
    }

    /// <summary>
    /// 将本地任务映射为期望的外部任务规格。返回 null 表示「无需外部触发」：
    /// 结构性无效（防御，正常调用方已先校验）或一次性任务已过期/无未来触发点。
    /// </summary>
    public ExternalTaskSpec? Map(
        TaskDefinition definition,
        string appExePath,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(appExePath);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (appExePath.Length == 0)
        {
            throw new ArgumentException("appExePath must not be empty.", nameof(appExePath));
        }

        if (TaskDefinitionValidator.GetStructuralError(definition) is not null)
        {
            return null;
        }

        var trigger = BuildTrigger(definition, now, timeZone);
        if (trigger is null)
        {
            return null;
        }

        var taskId = definition.Id;
        var name = TaskSyncNaming.BuildTaskName(taskId);
        var argument = BuildTriggerArgument(taskId);

        return new ExternalTaskSpec
        {
            Name = name,
            Description = BuildDescription(definition),
            Enabled = definition.IsEnabled,
            Trigger = trigger,
            AppExePath = appExePath,
            TriggerArgument = argument
        };
    }

    /// <summary>构建外部动作参数（只回调本地；含稳定本地 task id）。</summary>
    public static string BuildTriggerArgument(Guid taskId)
        => TriggerArgumentPrefix + " " + taskId.ToString("D");

    private ExternalTriggerSpec? BuildTrigger(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        return definition.Kind switch
        {
            TaskKind.Countdown => BuildOneTimeFromCalculator(definition, now, timeZone),
            TaskKind.TodayAt => BuildOneTimeFromCalculator(definition, now, timeZone),
            TaskKind.NextWorkday => BuildOneTimeFromCalculator(definition, now, timeZone),
            TaskKind.OneTime => BuildOneTimeFromCalculator(definition, now, timeZone),

            TaskKind.DailyAt when definition.TargetTimeOfDay is { } timeOfDay
                => new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.Daily,
                    StartTimeOfDay = timeOfDay
                },

            TaskKind.Weekdays when definition.Weekdays is { Count: > 0 } weekdays
                && definition.TargetTimeOfDay is { } weekdayTime
                => new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.Weekly,
                    StartTimeOfDay = weekdayTime,
                    DaysOfWeek = weekdays.OrderBy(day => day).Distinct().ToArray()
                },

            TaskKind.NthWorkdayOfMonth when definition.NthWorkday is { } nthWorkday
                && definition.TargetTimeOfDay is { } monthTime
                && MapNthWorkdayToWeek(nthWorkday) is { } week
                => new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.MonthlyOnWeekdays,
                    StartTimeOfDay = monthTime,
                    DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
                    NthWeek = week
                },

            TaskKind.Idle when IdleShutdownRule.ResolveThreshold(
                definition,
                IdleShutdownRule.GlobalDefaultThreshold) is { } idleThreshold
                => new ExternalTriggerSpec
                {
                    Kind = ExternalTriggerKind.Idle,
                    IdleDuration = idleThreshold
                },

            _ => null
        };
    }

    /// <summary>
    /// 一次性/按计算器得到未来触发点的任务：调用统一计算器；无未来触发（已过期/无工作日）
    /// 返回 null（不注册已无意义的外部触发）。触发时刻转为本机时区墙钟（外部任务运行在
    /// 所在机器时区）。
    /// </summary>
    private ExternalTriggerSpec? BuildOneTimeFromCalculator(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var schedule = _calculator.Calculate(definition, now, timeZone);
        if (!schedule.Succeeded || schedule.ScheduledFireTime is not { } fireTimeUtc)
        {
            return null;
        }

        var localStart = TimeZoneInfo.ConvertTime(fireTimeUtc, timeZone).DateTime;

        return new ExternalTriggerSpec
        {
            Kind = ExternalTriggerKind.OneTime,
            StartBoundary = localStart
        };
    }

    /// <summary>
    /// 第 N 个工作日（1..23，周一~周五）→ 周次（1..4 为 First..Fourth，5 为 Last）。
    /// 近似映射：外部任务仅作冗余候选，精确触发由本地应用的外部触发闸门裁决（本地永远是事实源）。
    /// </summary>
    private static int? MapNthWorkdayToWeek(int nthWorkday)
    {
        if (nthWorkday is < 1 or > 23)
        {
            return null;
        }

        var week = (nthWorkday - 1) / 5 + 1;
        return week > 4 ? 5 : week;
    }

    private static string BuildDescription(TaskDefinition definition)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "AutoShutdown V2 ({0}) external trigger for local task {1:D} ({2}, {3}). "
            + "The external task only invokes the local app; the local app is the only executor of power.",
            TaskSyncNaming.AppIdentifier,
            definition.Id,
            definition.Action,
            definition.Kind);
    }
}
