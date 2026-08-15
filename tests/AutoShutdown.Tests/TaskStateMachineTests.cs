using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class TaskStateMachineTests
{
    private readonly TaskStateMachine _machine = new();

    [Theory]
    [InlineData(TaskState.Idle, TaskState.Scheduled, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Scheduled, TaskState.Warning, TaskTransitionCause.WarningDue)]
    [InlineData(TaskState.Scheduled, TaskState.Executing, TaskTransitionCause.ExecuteDue)]
    [InlineData(TaskState.Scheduled, TaskState.Cancelled, TaskTransitionCause.CancelByUser)]
    [InlineData(TaskState.Scheduled, TaskState.Scheduled, TaskTransitionCause.Reschedule)]
    [InlineData(TaskState.Warning, TaskState.Scheduled, TaskTransitionCause.SnoozeByUser)]
    [InlineData(TaskState.Warning, TaskState.Cancelled, TaskTransitionCause.CancelByUser)]
    [InlineData(TaskState.Warning, TaskState.Executing, TaskTransitionCause.ExecuteDue)]
    [InlineData(TaskState.Executing, TaskState.Completed, TaskTransitionCause.PowerAccepted)]
    [InlineData(TaskState.Executing, TaskState.Failed, TaskTransitionCause.PowerFailed)]
    [InlineData(TaskState.Executing, TaskState.Interrupted, TaskTransitionCause.RecoveryInterrupted)]
    [InlineData(TaskState.Warning, TaskState.Interrupted, TaskTransitionCause.RecoveryInterrupted)]
    [InlineData(TaskState.Cancelled, TaskState.Idle, TaskTransitionCause.ClearTerminalState)]
    [InlineData(TaskState.Completed, TaskState.Idle, TaskTransitionCause.ClearTerminalState)]
    [InlineData(TaskState.Failed, TaskState.Idle, TaskTransitionCause.ClearTerminalState)]
    [InlineData(TaskState.Interrupted, TaskState.Idle, TaskTransitionCause.ClearTerminalState)]
    public void TryTransition_WhenTripletIsListed_ReturnsAllowed(
        TaskState current,
        TaskState target,
        TaskTransitionCause cause)
    {
        var result = _machine.TryTransition(current, target, cause);

        Assert.True(result.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.Allowed, result.DecisionCode);
        Assert.Equal(current, result.CurrentState);
        Assert.Equal(target, result.TargetState);
        Assert.Equal(cause, result.Cause);
    }

    [Theory]
    [InlineData(TaskState.Idle, TaskState.Scheduled, TaskTransitionCause.CancelByUser)]
    [InlineData(TaskState.Scheduled, TaskState.Warning, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Scheduled, TaskState.Executing, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Scheduled, TaskState.Cancelled, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Scheduled, TaskState.Scheduled, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Warning, TaskState.Scheduled, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Warning, TaskState.Cancelled, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Warning, TaskState.Executing, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Executing, TaskState.Completed, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Executing, TaskState.Failed, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Executing, TaskState.Interrupted, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Warning, TaskState.Interrupted, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Cancelled, TaskState.Idle, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Completed, TaskState.Idle, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Failed, TaskState.Idle, TaskTransitionCause.Schedule)]
    [InlineData(TaskState.Interrupted, TaskState.Idle, TaskTransitionCause.Schedule)]
    public void TryTransition_WhenCauseDoesNotMatchRequiredCause_ReturnsCauseMismatch(
        TaskState current,
        TaskState target,
        TaskTransitionCause wrongCause)
    {
        var result = _machine.TryTransition(current, target, wrongCause);

        Assert.False(result.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.CauseMismatch, result.DecisionCode);
    }

    [Theory]
    [InlineData(TaskState.Idle, TaskState.Executing)]
    [InlineData(TaskState.Idle, TaskState.Completed)]
    [InlineData(TaskState.Scheduled, TaskState.Completed)]
    [InlineData(TaskState.Warning, TaskState.Completed)]
    [InlineData(TaskState.Executing, TaskState.Scheduled)]
    [InlineData(TaskState.Cancelled, TaskState.Scheduled)]
    [InlineData(TaskState.Completed, TaskState.Executing)]
    [InlineData(TaskState.Failed, TaskState.Executing)]
    [InlineData(TaskState.Interrupted, TaskState.Executing)]
    public void TryTransition_WhenStatePairIsNotListed_ReturnsTransitionNotAllowed(
        TaskState current,
        TaskState target)
    {
        var result = _machine.TryTransition(current, target, TaskTransitionCause.Schedule);

        Assert.False(result.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.TransitionNotAllowed, result.DecisionCode);
    }

    [Fact]
    public void TryTransition_WhenCurrentStateIsUnknown_ReturnsUnknownCurrentState()
    {
        var result = _machine.TryTransition(
            TaskState.Unknown,
            TaskState.Idle,
            TaskTransitionCause.Schedule);

        Assert.False(result.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.UnknownCurrentState, result.DecisionCode);
    }

    [Fact]
    public void TryTransition_WhenTargetStateIsUnknown_ReturnsUnknownTargetState()
    {
        var result = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Unknown,
            TaskTransitionCause.Schedule);

        Assert.False(result.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.UnknownTargetState, result.DecisionCode);
    }

    [Fact]
    public void TryTransition_WhenCauseIsUnknown_ReturnsUnknownCause()
    {
        var result = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Scheduled,
            TaskTransitionCause.Unknown);

        Assert.False(result.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.UnknownCause, result.DecisionCode);
    }

    [Fact]
    public void TryTransition_AppliesDecisionChecksInStableOrder()
    {
        var allUnknown = _machine.TryTransition(
            TaskState.Unknown,
            TaskState.Unknown,
            TaskTransitionCause.Unknown);
        Assert.Equal(TaskTransitionDecisionCode.UnknownCurrentState, allUnknown.DecisionCode);

        var unknownTarget = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Unknown,
            TaskTransitionCause.Unknown);
        Assert.Equal(TaskTransitionDecisionCode.UnknownTargetState, unknownTarget.DecisionCode);

        var unknownCause = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Scheduled,
            TaskTransitionCause.Unknown);
        Assert.Equal(TaskTransitionDecisionCode.UnknownCause, unknownCause.DecisionCode);

        var noPair = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Executing,
            TaskTransitionCause.Schedule);
        Assert.Equal(TaskTransitionDecisionCode.TransitionNotAllowed, noPair.DecisionCode);

        var badCause = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Scheduled,
            TaskTransitionCause.WarningDue);
        Assert.Equal(TaskTransitionDecisionCode.CauseMismatch, badCause.DecisionCode);
    }

    [Theory]
    [InlineData(TaskState.Cancelled)]
    [InlineData(TaskState.Completed)]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Interrupted)]
    public void TryTransition_TerminalStateToIdle_OnlyAcceptsClearTerminalState(TaskState terminal)
    {
        var allowed = _machine.TryTransition(
            terminal,
            TaskState.Idle,
            TaskTransitionCause.ClearTerminalState);
        Assert.True(allowed.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.Allowed, allowed.DecisionCode);

        var rejected = _machine.TryTransition(
            terminal,
            TaskState.Idle,
            TaskTransitionCause.Schedule);
        Assert.False(rejected.Allowed);
        Assert.Equal(TaskTransitionDecisionCode.CauseMismatch, rejected.DecisionCode);
    }

    [Fact]
    public void TryTransition_ScheduledToScheduled_OnlyAcceptsReschedule()
    {
        var allowed = _machine.TryTransition(
            TaskState.Scheduled,
            TaskState.Scheduled,
            TaskTransitionCause.Reschedule);
        Assert.True(allowed.Allowed);

        var rejected = _machine.TryTransition(
            TaskState.Scheduled,
            TaskState.Scheduled,
            TaskTransitionCause.WarningDue);
        Assert.Equal(TaskTransitionDecisionCode.CauseMismatch, rejected.DecisionCode);
    }

    [Fact]
    public void TryTransition_WarningToScheduled_OnlyAcceptsSnoozeByUser()
    {
        var allowed = _machine.TryTransition(
            TaskState.Warning,
            TaskState.Scheduled,
            TaskTransitionCause.SnoozeByUser);
        Assert.True(allowed.Allowed);

        var rejected = _machine.TryTransition(
            TaskState.Warning,
            TaskState.Scheduled,
            TaskTransitionCause.Schedule);
        Assert.Equal(TaskTransitionDecisionCode.CauseMismatch, rejected.DecisionCode);
    }

    [Fact]
    public void TryTransition_RepeatedCallsReturnIdenticalResults()
    {
        var first = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Scheduled,
            TaskTransitionCause.Schedule);
        var second = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Scheduled,
            TaskTransitionCause.Schedule);
        Assert.Equal(first, second);

        var firstRejected = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Completed,
            TaskTransitionCause.Schedule);
        var secondRejected = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Completed,
            TaskTransitionCause.Schedule);
        Assert.Equal(firstRejected, secondRejected);
    }

    [Fact]
    public void TryTransition_ResultContainsFullInputAndNonEmptyMessage()
    {
        var allowed = _machine.TryTransition(
            TaskState.Warning,
            TaskState.Executing,
            TaskTransitionCause.ExecuteDue);
        Assert.True(allowed.Allowed);
        Assert.Equal(TaskState.Warning, allowed.CurrentState);
        Assert.Equal(TaskState.Executing, allowed.TargetState);
        Assert.Equal(TaskTransitionCause.ExecuteDue, allowed.Cause);
        Assert.Equal(TaskTransitionDecisionCode.Allowed, allowed.DecisionCode);
        Assert.False(string.IsNullOrEmpty(allowed.Message));

        var rejected = _machine.TryTransition(
            TaskState.Idle,
            TaskState.Completed,
            TaskTransitionCause.Schedule);
        Assert.False(rejected.Allowed);
        Assert.Equal(TaskState.Idle, rejected.CurrentState);
        Assert.Equal(TaskState.Completed, rejected.TargetState);
        Assert.Equal(TaskTransitionCause.Schedule, rejected.Cause);
        Assert.Equal(TaskTransitionDecisionCode.TransitionNotAllowed, rejected.DecisionCode);
        Assert.False(string.IsNullOrEmpty(rejected.Message));
    }
}
