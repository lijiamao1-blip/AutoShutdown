using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Tasks;

public sealed record TaskDefinition
{
    public Guid Id { get; init; }

    public TaskKind Kind { get; init; } = TaskKind.Unknown;

    public PowerAction Action { get; init; } = PowerAction.Unknown;

    public TimeSpan? CountdownDuration { get; init; }

    /// <summary>
    /// 目标时刻（本地墙钟时间）。用于 TodayAt/DailyAt（既有）以及
    /// Weekdays/NextWorkday/NthWorkdayOfMonth（S14 新增）的每日触发时刻。
    /// </summary>
    public TimeOnly? TargetTimeOfDay { get; init; }

    /// <summary>每周工作日的触发星期集合（S14，Weekdays 专用；例如周一~周五 = 1..5）。</summary>
    public IReadOnlyList<DayOfWeek>? Weekdays { get; init; }

    /// <summary>每月第 N 个工作日（S14，NthWorkdayOfMonth 专用；合法 1..23）。</summary>
    public int? NthWorkday { get; init; }

    /// <summary>一次性指定日期时间（S14，OneTime 专用）。以本地墙钟时间存储，不含时区。</summary>
    public DateTime? OneTimeDateTime { get; init; }

    /// <summary>节假日/例外日集合（S14）。周期规则遇节假日跳过；一次性日期不受影响。</summary>
    public IReadOnlyList<DateOnly>? HolidayDates { get; init; }

    /// <summary>
    /// 空闲触发阈值（秒，S15，TaskKind.Idle 专用）。null 表示继承全局默认阈值。
    /// 指定时必须为正，否则视为无效（默认不触发，绝不静默回退）。
    /// </summary>
    public int? IdleThresholdSeconds { get; init; }

    public int? WarningSeconds { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>真实电源执行是否已获用户明确确认（双闸门之二）。默认 false。</summary>
    public bool RealPowerConfirmed { get; init; }

    /// <summary>
    /// 任务级「使用无人值守」选择（S20-D1）。默认 false（关闭，维持人工确认路径）。
    /// 仅当本地无人值守授权有效且与所选电源动作匹配时，UI 才允许置真；置真后创建
    /// RealPowerConfirmed=false 的任务，由调度器在倒计时边界做无人值守等效确认裁决。
    /// </summary>
    public bool UseUnattended { get; init; }

    /// <summary>
    /// 任务启用状态。默认 true（启用）。false 表示禁用，调度器跳过该任务。
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// 运行时任务仲裁优先级（GATE-Q4 裁决）：int，合法范围 0~100，默认 0。
    /// 数值越大优先级越高；超出范围拒绝、不静默截断；S13 暂不向 UI 开放编辑。
    /// 注意：此字段是运行时仲裁优先级，与需求表中的 P0/P1 开发优先级无关。
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// WoL 目标机器 id（S21，Action==WakeOnLan 时必填）。指向 target-machines.json 中的
    /// 目标机器；仅向用户显式配置的局域网目标发送。电源动作禁止携带该字段。
    /// </summary>
    public Guid? TargetMachineId { get; init; }

    /// <summary>
    /// 一次性 RTC 唤醒时间（UTC，S21）。仅电源动作可用（Shutdown/Restart 需平台支持
    /// S4/S5 唤醒，Sleep/Hibernate 至少支持 S3/S4 唤醒）；WoL 任务禁止携带该字段。
    /// 执行时经 TaskInstance 快照传入 ShutdownWorkflow，由 Pre-Pipeline 的 RtcWakeAction
    /// 作为受控关机前步骤处理。
    /// </summary>
    public DateTimeOffset? RtcWakeTimeUtc { get; init; }
}
