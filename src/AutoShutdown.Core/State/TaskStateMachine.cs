using System.Collections.Frozen;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.State;

public sealed class TaskStateMachine : ITaskStateMachine
{
    private static readonly FrozenDictionary<(TaskState Current, TaskState Target), TaskTransitionCause> AllowedTransitions =
        new Dictionary<(TaskState, TaskState), TaskTransitionCause>
        {
            [(TaskState.Idle, TaskState.Scheduled)] = TaskTransitionCause.Schedule,
            [(TaskState.Scheduled, TaskState.Warning)] = TaskTransitionCause.WarningDue,
            [(TaskState.Scheduled, TaskState.Executing)] = TaskTransitionCause.ExecuteDue,
            [(TaskState.Scheduled, TaskState.Cancelled)] = TaskTransitionCause.CancelByUser,
            [(TaskState.Scheduled, TaskState.Scheduled)] = TaskTransitionCause.Reschedule,
            [(TaskState.Warning, TaskState.Scheduled)] = TaskTransitionCause.SnoozeByUser,
            [(TaskState.Warning, TaskState.Cancelled)] = TaskTransitionCause.CancelByUser,
            [(TaskState.Warning, TaskState.Executing)] = TaskTransitionCause.ExecuteDue,
            [(TaskState.Executing, TaskState.Completed)] = TaskTransitionCause.PowerAccepted,
            [(TaskState.Executing, TaskState.Failed)] = TaskTransitionCause.PowerFailed,
            [(TaskState.Executing, TaskState.Interrupted)] = TaskTransitionCause.RecoveryInterrupted,
            [(TaskState.Warning, TaskState.Interrupted)] = TaskTransitionCause.RecoveryInterrupted,
            [(TaskState.Cancelled, TaskState.Idle)] = TaskTransitionCause.ClearTerminalState,
            [(TaskState.Completed, TaskState.Idle)] = TaskTransitionCause.ClearTerminalState,
            [(TaskState.Failed, TaskState.Idle)] = TaskTransitionCause.ClearTerminalState,
            [(TaskState.Interrupted, TaskState.Idle)] = TaskTransitionCause.ClearTerminalState
        }.ToFrozenDictionary();

    public TaskTransitionResult TryTransition(
        TaskState current,
        TaskState target,
        TaskTransitionCause cause)
    {
        if (current == TaskState.Unknown)
        {
            return Reject(
                TaskTransitionDecisionCode.UnknownCurrentState,
                current,
                target,
                cause,
                "The current state is Unknown.");
        }

        if (target == TaskState.Unknown)
        {
            return Reject(
                TaskTransitionDecisionCode.UnknownTargetState,
                current,
                target,
                cause,
                "The target state is Unknown.");
        }

        if (cause == TaskTransitionCause.Unknown)
        {
            return Reject(
                TaskTransitionDecisionCode.UnknownCause,
                current,
                target,
                cause,
                "The transition cause is Unknown.");
        }

        if (!AllowedTransitions.TryGetValue((current, target), out var requiredCause))
        {
            return Reject(
                TaskTransitionDecisionCode.TransitionNotAllowed,
                current,
                target,
                cause,
                $"The transition from {current} to {target} is not allowed.");
        }

        if (cause != requiredCause)
        {
            return Reject(
                TaskTransitionDecisionCode.CauseMismatch,
                current,
                target,
                cause,
                $"The cause {cause} does not match the required cause {requiredCause} for {current} to {target}.");
        }

        return new TaskTransitionResult
        {
            Allowed = true,
            CurrentState = current,
            TargetState = target,
            Cause = cause,
            DecisionCode = TaskTransitionDecisionCode.Allowed,
            Message = $"The transition from {current} to {target} with cause {cause} is allowed."
        };
    }

    private static TaskTransitionResult Reject(
        TaskTransitionDecisionCode decisionCode,
        TaskState current,
        TaskState target,
        TaskTransitionCause cause,
        string message) => new()
        {
            Allowed = false,
            CurrentState = current,
            TargetState = target,
            Cause = cause,
            DecisionCode = decisionCode,
            Message = message
        };
}
