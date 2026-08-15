using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Tasks;

public sealed record TaskDefinition
{
    public Guid Id { get; init; }

    public TaskKind Kind { get; init; } = TaskKind.Unknown;

    public PowerAction Action { get; init; } = PowerAction.Unknown;

    public TimeSpan? CountdownDuration { get; init; }

    public TimeOnly? TargetTimeOfDay { get; init; }

    public int? WarningSeconds { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>真实电源执行是否已获用户明确确认（双闸门之二）。默认 false。</summary>
    public bool RealPowerConfirmed { get; init; }

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
}
