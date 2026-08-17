using AutoShutdown.Core.State;
using AutoShutdown.Core.Unattended;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S20 检查点 2（输入与倒计时）：等效确认评估器的取消胜出 / fail-closed / 单次语义。
/// </summary>
public sealed class S20_UnattendedConfirmationEvaluatorTests
{
    private static readonly UnattendedAuthorizationDecision Authorized = UnattendedAuthorizationDecision.Granted(
        new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 1,
            AuthorizedAtUtc = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            AuthorizedAction = PowerAction.Shutdown
        });

    private static readonly UnattendedAuthorizationDecision Expired = UnattendedAuthorizationDecision.Denied(
        UnattendedPolicyStatus.Expired,
        "The unattended authorization has expired.");

    private static readonly UnattendedAuthorizationDecision Corrupt = UnattendedAuthorizationDecision.Denied(
        UnattendedPolicyStatus.Corrupt,
        "The unattended authorization record is corrupt.");

    private readonly UnattendedConfirmationEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WhenAuthorizedAndConfirming_IsEquivalentConfirmation()
    {
        var instance = CreateInstance(TaskInstanceState.Confirming);

        var decision = _evaluator.Evaluate(Authorized, instance);

        Assert.Equal(UnattendedConfirmationOutcome.EquivalentConfirmation, decision.Outcome);
        Assert.True(decision.AllowsPower);
        Assert.NotNull(decision.Authorization);
    }

    [Fact]
    public void Evaluate_WhenAuthorizedButCancelled_DeniedCancelWins()
    {
        var instance = CreateInstance(TaskInstanceState.Cancelled);

        var decision = _evaluator.Evaluate(Authorized, instance);

        Assert.Equal(UnattendedConfirmationOutcome.Denied, decision.Outcome);
        Assert.False(decision.AllowsPower);
        Assert.Null(decision.Authorization);
    }

    [Fact]
    public void Evaluate_WhenAuthorizedButFaulted_Denied()
    {
        var decision = _evaluator.Evaluate(Authorized, CreateInstance(TaskInstanceState.Faulted));

        Assert.Equal(UnattendedConfirmationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Evaluate_WhenAuthorizedButInterrupted_Denied()
    {
        var decision = _evaluator.Evaluate(Authorized, CreateInstance(TaskInstanceState.Interrupted));

        Assert.Equal(UnattendedConfirmationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Evaluate_WhenAuthorizedButAlreadyExecuted_DeniedNoDuplicate()
    {
        // 终态 Executed：绝不重复等效确认（单次执行语义）。
        var decision = _evaluator.Evaluate(Authorized, CreateInstance(TaskInstanceState.Executed));

        Assert.Equal(UnattendedConfirmationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Evaluate_WhenIdleRecovered_DeniedUserActivityWins()
    {
        // 用户活动（输入恢复取消）优先于无人值守授权。
        var instance = CreateInstance(TaskInstanceState.Cancelled) with { IsIdleRecovered = true };

        var decision = _evaluator.Evaluate(Authorized, instance);

        Assert.Equal(UnattendedConfirmationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Evaluate_WhenExpired_DeniedFailClosed()
    {
        var decision = _evaluator.Evaluate(Expired, CreateInstance(TaskInstanceState.Confirming));

        Assert.Equal(UnattendedConfirmationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Evaluate_WhenCorrupt_DeniedFailClosed()
    {
        var decision = _evaluator.Evaluate(Corrupt, CreateInstance(TaskInstanceState.Confirming));

        Assert.Equal(UnattendedConfirmationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Evaluate_IsDeterministic_AndSingleShot()
    {
        var instance = CreateInstance(TaskInstanceState.Confirming);

        // 纯函数：同一输入重复评估得到同一决策，评估器自身无副作用、不产生重复执行。
        var first = _evaluator.Evaluate(Authorized, instance);
        var second = _evaluator.Evaluate(Authorized, instance);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Reason, second.Reason);

        // 一旦实例进入终态（执行完成），后续评估即被拒绝 —— 单次等效确认。
        var afterExecution = _evaluator.Evaluate(Authorized, instance with { State = TaskInstanceState.Executed });
        Assert.Equal(UnattendedConfirmationOutcome.Denied, afterExecution.Outcome);
    }

    private static TaskInstance CreateInstance(TaskInstanceState state)
        => new()
        {
            InstanceId = Guid.NewGuid(),
            SourceTaskId = Guid.NewGuid(),
            ActionSnapshot = PowerAction.Shutdown,
            State = state,
            HasExecuted = state == TaskInstanceState.Executing || state == TaskInstanceState.Executed
        };
}
