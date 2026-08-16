using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Idle;

/// <summary>
/// 空闲关机规则：决定阈值（每任务独立阈值或继承全局策略）。
/// 只做阈值判定，不读系统时间、不接触电源；触发与仲裁交给 S13 调度。
/// </summary>
public static class IdleShutdownRule
{
    /// <summary>全局默认空闲阈值（30 分钟）。Idle 任务未指定自身阈值时继承。</summary>
    public static readonly TimeSpan GlobalDefaultThreshold = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 解析有效阈值：任务自身 IdleThresholdSeconds 优先（必须为正，非法视为无效返回 null）；
    /// 未指定则继承 globalDefault。两者皆无/非法返回 null（默认不触发）。
    /// </summary>
    public static TimeSpan? ResolveThreshold(TaskDefinition definition, TimeSpan? globalDefault)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.IdleThresholdSeconds is { } seconds)
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        return globalDefault;
    }

    /// <summary>
    /// 判断任务是否空闲到期。检测失败（idleDuration=null）、阈值缺失、任务非 Idle 均返回 false
    /// （检测失败默认不触发，绝不猜空闲时长）。
    /// </summary>
    public static bool IsIdleDue(
        TaskDefinition definition,
        TimeSpan? globalDefault,
        TimeSpan? idleDuration)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Kind != TaskKind.Idle)
        {
            return false;
        }

        if (idleDuration is null)
        {
            return false;
        }

        var threshold = ResolveThreshold(definition, globalDefault);
        return threshold is { } value && idleDuration.Value >= value;
    }
}
