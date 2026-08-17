using System.Globalization;
using System.Text;

namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

/// <summary>
/// 外部（Windows 任务计划程序）触发器的平台无关规格（S22 单向同步）。
/// 只描述「本地任务 → 外部触发」所需的触发形态；时区一律以本地墙钟表达
/// （外部任务运行在所在机器时区，与本地任务语义一致）。
/// </summary>
public enum ExternalTriggerKind
{
    Unknown = 0,

    /// <summary>一次性：绝对本地开始时刻（Countdown/TodayAt/NextWorkday/OneTime）。</summary>
    OneTime = 1,

    /// <summary>每日固定时刻（DailyAt）。</summary>
    Daily = 2,

    /// <summary>每周指定星期固定时刻（Weekdays）。</summary>
    Weekly = 3,

    /// <summary>每月第 N 个工作日固定时刻（NthWorkdayOfMonth，NthWeek=1..5，5 表示「Last」周）。</summary>
    MonthlyOnWeekdays = 4,

    /// <summary>空闲触发（Idle；IdleDuration 为阈值）。</summary>
    Idle = 5
}

public sealed record ExternalTriggerSpec
{
    public ExternalTriggerKind Kind { get; init; } = ExternalTriggerKind.Unknown;

    /// <summary>OneTime：本地墙钟开始时刻（含日期）。</summary>
    public DateTime? StartBoundary { get; init; }

    /// <summary>Daily/Weekly/MonthlyOnWeekdays：每日触发时刻。</summary>
    public TimeOnly? StartTimeOfDay { get; init; }

    /// <summary>Weekly：触发星期集合（按 DayOfWeek 升序，唯一）。</summary>
    public IReadOnlyList<DayOfWeek>? DaysOfWeek { get; init; }

    /// <summary>MonthlyOnWeekdays：第 N 周（1..4 为 First..Fourth，5 表示 Last）。</summary>
    public int? NthWeek { get; init; }

    /// <summary>Idle：空闲时长阈值。</summary>
    public TimeSpan? IdleDuration { get; init; }
}

/// <summary>
/// 本地任务映射出的「期望外部任务」规格（平台无关）。动作永远是「回调本地应用」，
/// 绝不直接携带电源命令；本地应用是唯一的事实源与唯一电源出口。
/// </summary>
public sealed record ExternalTaskSpec
{
    public string Name { get; init; } = string.Empty;

    /// <summary>审计用描述（含应用标识、本地 task id、动作与触发概要）。</summary>
    public string Description { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public ExternalTriggerSpec Trigger { get; init; } = new();

    /// <summary>外部任务动作：本地应用可执行文件路径。</summary>
    public string AppExePath { get; init; } = string.Empty;

    /// <summary>外部任务动作参数：只回调本地（例如 --trigger-task &lt;id&gt;）。</summary>
    public string TriggerArgument { get; init; } = string.Empty;
}

/// <summary>
/// 外部任务的规范化触发签名（供幂等比较）。mapper 侧由期望规格生成；
/// 适配器侧由系统查询到的真实触发器字段经同一生成器生成——两边同构才能可靠判定「是否需更新」。
/// 周期触发的签名只含形态（时刻/星期/周），不含绝对开始日期，避免「注册日与重同步日不同」造成
/// 每日假更新；一次性触发含绝对本地时刻。
/// </summary>
public static class ExternalTriggerSignature
{
    public static string Build(ExternalTriggerSpec trigger)
        => Build(
            trigger.Kind,
            trigger.StartBoundary,
            trigger.StartTimeOfDay,
            trigger.DaysOfWeek,
            trigger.NthWeek,
            trigger.IdleDuration);

    public static string Build(
        ExternalTriggerKind kind,
        DateTime? startBoundary,
        TimeOnly? startTimeOfDay,
        IReadOnlyList<DayOfWeek>? daysOfWeek,
        int? nthWeek,
        TimeSpan? idleDuration)
    {
        var builder = new StringBuilder();
        builder.Append(kind.ToString());

        switch (kind)
        {
            case ExternalTriggerKind.OneTime:
                builder.Append('@');
                builder.Append(startBoundary is { } boundary
                    ? boundary.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : "missing");
                break;

            case ExternalTriggerKind.Daily:
                builder.Append('@');
                builder.Append(FormatTimeOfDay(startTimeOfDay));
                break;

            case ExternalTriggerKind.Weekly:
                builder.Append('@');
                builder.Append(FormatTimeOfDay(startTimeOfDay));
                builder.Append(':');
                if (daysOfWeek is not null)
                {
                    builder.Append(string.Join(",", daysOfWeek.OrderBy(day => day).Select(day => ((int)day).ToString(CultureInfo.InvariantCulture))));
                }

                break;

            case ExternalTriggerKind.MonthlyOnWeekdays:
                builder.Append('@');
                builder.Append(FormatTimeOfDay(startTimeOfDay));
                builder.Append(':');
                builder.Append(nthWeek?.ToString(CultureInfo.InvariantCulture) ?? "missing");
                break;

            case ExternalTriggerKind.Idle:
                builder.Append('@');
                builder.Append(idleDuration is { } duration
                    ? ((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture)
                    : "missing");
                break;

            default:
                builder.Append("@unknown");
                break;
        }

        return builder.ToString();
    }

    private static string FormatTimeOfDay(TimeOnly? time)
        => time is { } value
            ? value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "missing";
}
