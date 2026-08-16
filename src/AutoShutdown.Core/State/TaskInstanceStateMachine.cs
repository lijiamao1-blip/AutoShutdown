using System.Collections.Frozen;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.State;

/// <summary>
/// V2 8 态任务实例状态机（架构 V2-DRAFT-004 S13 §5，GATE-A2 裁决）。
/// 白名单 = 15 条唯一 (当前→目标) 状态边；每条边接受一个或多个触发原因
/// （waiting→cancelled 接受 CancelByUser 与 OneTimeExpired 两个触发场景，修正 6d）。
/// 终态（cancelled/faulted/interrupted）一律不可逆，拒绝一切出边转换。
/// 纯逻辑：无日志依赖、无日志写入；调用方负责 Error 审计日志。
/// S16 一致性修复：V2-DRAFT-004 全局流程次序为 Countdown→Confirm→Pre-Pipeline→Power，
/// 故将状态序修正为 waiting→confirming（Countdown）→running（Pre-Pipeline）→executing（Power）。
/// </summary>
public sealed class TaskInstanceStateMachine : ITaskInstanceStateMachine
{
    private static readonly FrozenDictionary<(TaskInstanceState Current, TaskInstanceState Target), FrozenSet<TaskInstanceStateTransitionCause>> AllowedTransitions =
        new Dictionary<(TaskInstanceState, TaskInstanceState), FrozenSet<TaskInstanceStateTransitionCause>>
        {
            // waiting：3 出
            [(TaskInstanceState.Waiting, TaskInstanceState.Confirming)] = C(TaskInstanceStateTransitionCause.ScheduleTriggered),
            [(TaskInstanceState.Waiting, TaskInstanceState.Cancelled)] = C(
                TaskInstanceStateTransitionCause.CancelByUser,
                TaskInstanceStateTransitionCause.OneTimeExpired),
            [(TaskInstanceState.Waiting, TaskInstanceState.Faulted)] = C(TaskInstanceStateTransitionCause.ConfigCorrupt),

            // confirming：4 出（Countdown/确认窗口）
            [(TaskInstanceState.Confirming, TaskInstanceState.Running)] = C(TaskInstanceStateTransitionCause.PowerConfirmed),
            [(TaskInstanceState.Confirming, TaskInstanceState.Cancelled)] = C(TaskInstanceStateTransitionCause.CancelledDuringConfirmation),
            [(TaskInstanceState.Confirming, TaskInstanceState.Interrupted)] = C(TaskInstanceStateTransitionCause.CrashRecovered),
            [(TaskInstanceState.Confirming, TaskInstanceState.Faulted)] = C(TaskInstanceStateTransitionCause.ConfigCorrupt),

            // running：4 出（Pre-Pipeline）
            [(TaskInstanceState.Running, TaskInstanceState.Executing)] = C(TaskInstanceStateTransitionCause.PipelineCompleted),
            [(TaskInstanceState.Running, TaskInstanceState.Cancelled)] = C(TaskInstanceStateTransitionCause.PipelineFailedBlocked),
            [(TaskInstanceState.Running, TaskInstanceState.Interrupted)] = C(TaskInstanceStateTransitionCause.CrashRecovered),
            [(TaskInstanceState.Running, TaskInstanceState.Faulted)] = C(TaskInstanceStateTransitionCause.RuntimeConfigCorrupt),

            // executing：3 出（电源操作）
            [(TaskInstanceState.Executing, TaskInstanceState.Executed)] = C(TaskInstanceStateTransitionCause.PowerCompleted),
            [(TaskInstanceState.Executing, TaskInstanceState.Interrupted)] = C(TaskInstanceStateTransitionCause.CrashRecovered),
            [(TaskInstanceState.Executing, TaskInstanceState.Faulted)] = C(TaskInstanceStateTransitionCause.PowerFailed),

            // executed：1 出（重排，刷新 StageToken）
            [(TaskInstanceState.Executed, TaskInstanceState.Waiting)] = C(TaskInstanceStateTransitionCause.Reschedule)
        }.ToFrozenDictionary();

    public TaskInstanceStateTransitionResult TryTransition(
        TaskInstanceState current,
        TaskInstanceState target,
        TaskInstanceStateTransitionCause cause,
        string? source = null)
    {
        var timestamp = DateTimeOffset.UtcNow;

        if (current == TaskInstanceState.Unknown)
        {
            return Reject(
                TaskInstanceStateTransitionDecisionCode.UnknownCurrentState,
                current, target, cause, source, timestamp,
                "The current state is Unknown.");
        }

        if (target == TaskInstanceState.Unknown)
        {
            return Reject(
                TaskInstanceStateTransitionDecisionCode.UnknownTargetState,
                current, target, cause, source, timestamp,
                "The target state is Unknown.");
        }

        if (cause == TaskInstanceStateTransitionCause.Unknown)
        {
            return Reject(
                TaskInstanceStateTransitionDecisionCode.UnknownCause,
                current, target, cause, source, timestamp,
                "The transition cause is Unknown.");
        }

        if (!AllowedTransitions.TryGetValue((current, target), out var allowedCauses))
        {
            return Reject(
                TaskInstanceStateTransitionDecisionCode.TransitionNotAllowed,
                current, target, cause, source, timestamp,
                $"The transition from {current} to {target} is not in the whitelist.");
        }

        if (!allowedCauses.Contains(cause))
        {
            return Reject(
                TaskInstanceStateTransitionDecisionCode.CauseMismatch,
                current, target, cause, source, timestamp,
                $"The cause {cause} is not a valid trigger for the transition from {current} to {target}.");
        }

        return new TaskInstanceStateTransitionResult
        {
            Allowed = true,
            CurrentState = current,
            TargetState = target,
            Cause = cause,
            DecisionCode = TaskInstanceStateTransitionDecisionCode.Allowed,
            Source = source,
            TimestampUtc = timestamp,
            Reason = $"The transition from {current} to {target} with cause {cause} is allowed."
        };
    }

    private static TaskInstanceStateTransitionResult Reject(
        TaskInstanceStateTransitionDecisionCode decisionCode,
        TaskInstanceState current,
        TaskInstanceState target,
        TaskInstanceStateTransitionCause cause,
        string? source,
        DateTimeOffset timestamp,
        string reason) => new()
        {
            Allowed = false,
            CurrentState = current,
            TargetState = target,
            Cause = cause,
            DecisionCode = decisionCode,
            Source = source,
            TimestampUtc = timestamp,
            Reason = reason
        };

    private static FrozenSet<TaskInstanceStateTransitionCause> C(params TaskInstanceStateTransitionCause[] causes)
        => causes.ToFrozenSet();
}
