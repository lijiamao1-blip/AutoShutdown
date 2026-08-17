using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

public enum ExternalTriggerGateVerdict
{
    Unknown = 0,

    /// <summary>外部触发落在本地调度窗口内（或 Idle 触发本身即空闲证据）：允许交回本地 Workflow。</summary>
    Allowed = 1,

    /// <summary>外部触发偏离本地调度窗口（近似映射误触发/伪造/滞后）：拒绝，本地调度器才是事实源。</summary>
    OutsideWindow = 2,

    /// <summary>本地无法算出未来触发点（一次性已过期等）：拒绝。</summary>
    NoSchedule = 3
}

public sealed record ExternalTriggerGateResult
{
    public ExternalTriggerGateVerdict Verdict { get; init; } = ExternalTriggerGateVerdict.Unknown;

    public DateTimeOffset? ExpectedFireTimeUtc { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// 外部触发时间闸门（S22 CP4）：以本地调度器（唯一事实源）计算出的期望触发窗口，裁决外部
/// 触发回调是否允许执行。精确补偿映射器近似形态（如 NthWorkdayOfMonth 周次近似）造成的
/// 误触发；拒绝会静默吞掉一次多余回调（本地调度器运行时会自行执行真实触发）。纯逻辑、无 IO。
/// </summary>
public sealed class ExternalTriggerScheduleGate
{
    /// <summary>默认容差：外部触发允许相对本地期望触发点 ±5 分钟（进程启动/排队抖动）。</summary>
    public static TimeSpan DefaultTolerance { get; } = TimeSpan.FromMinutes(5);

    private readonly INextExecutionCalculator _calculator;

    public ExternalTriggerScheduleGate(INextExecutionCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        _calculator = calculator;
    }

    public ExternalTriggerGateResult Evaluate(
        TaskDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(timeZone);

        var window = tolerance ?? DefaultTolerance;

        // Idle 触发：Windows 只在系统空闲期满后触发，其本身即空闲证据；无固定触发时刻可比对。
        // 电源仍受 ShutdownWorkflow 双闸门约束；本地调度器运行时也会处理 Idle 任务。
        if (definition.Kind == TaskKind.Idle)
        {
            return new ExternalTriggerGateResult
            {
                Verdict = ExternalTriggerGateVerdict.Allowed,
                Reason = "Idle external trigger fires only after the system has been idle for the threshold."
            };
        }

        // 从 now-容差 起算期望触发点：避免「正好等于边界」的严格大于/等于歧义，
        // 并容忍启动/排队造成的几分钟滞后（一次性任务已过点仍算窗口内）。
        var searchStart = now.Add(-window);
        var schedule = _calculator.Calculate(definition, searchStart, timeZone);
        if (!schedule.Succeeded || schedule.ScheduledFireTime is not { } expectedFireUtc)
        {
            return new ExternalTriggerGateResult
            {
                Verdict = ExternalTriggerGateVerdict.NoSchedule,
                Reason = schedule.Message
            };
        }

        var deviation = (expectedFireUtc.ToUniversalTime() - now.ToUniversalTime()).Duration();
        if (deviation <= window)
        {
            return new ExternalTriggerGateResult
            {
                Verdict = ExternalTriggerGateVerdict.Allowed,
                ExpectedFireTimeUtc = expectedFireUtc
            };
        }

        return new ExternalTriggerGateResult
        {
            Verdict = ExternalTriggerGateVerdict.OutsideWindow,
            ExpectedFireTimeUtc = expectedFireUtc,
            Reason = $"Expected fire at {expectedFireUtc:o} but external trigger at {now:o} "
                + $"deviates by {deviation:hh\\:mm\\:ss}, exceeding tolerance {window:hh\\:mm\\:ss}."
        };
    }
}
