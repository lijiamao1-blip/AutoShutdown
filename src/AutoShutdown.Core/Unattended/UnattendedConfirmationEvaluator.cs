using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Unattended;

/// <summary>
/// 无人值守等效确认评估器（S20 检查点 2：输入与倒计时）。
///
/// 在 Countdown（Confirming）到期边界统一评估：无人值守等效确认仅用于替代
/// 人工确认事实（双闸门之二），绝不改变冻结状态机与排程引擎。本类为纯函数、无副作用，
/// 单次执行的唯一性由状态机 + <see cref="TaskInstance.HasExecuted"/> 保证；
/// 重复调用同一输入得到同一决策，绝不因评估器自身产生重复执行。
///
/// 优先级（严格执行书）：
/// <list type="number">
/// <item>用户活动 / 显式取消优先：实例已终结（Cancelled/Faulted/Interrupted/Executed）
///     或已因输入恢复而取消（<see cref="TaskInstance.IsIdleRecovered"/>）→ 拒绝。</item>
/// <item>授权失效（Disabled/Revoked/Expired/Corrupt/NotFound/UnsupportedVersion/Unavailable/
///     动作不匹配）→ 拒绝（fail-closed）。</item>
/// <item>仅当实例仍在 Confirming 且授权有效 → 等效确认。</item>
/// </list>
/// </summary>
public sealed class UnattendedConfirmationEvaluator
{
    /// <summary>
    /// 在倒计时边界评估是否可给出无人值守等效确认。等价于「取消胜出 + 一次有效等效确认」。
    /// </summary>
    /// <param name="authorization">已评估的无人值守授权决策（来自 <see cref="IUnattendedPolicyService.EvaluateAsync"/>）。</param>
    /// <param name="instance">待确认的运行实例。</param>
    public UnattendedConfirmationDecision Evaluate(
        UnattendedAuthorizationDecision authorization,
        TaskInstance instance)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(instance);

        // 规则 1：取消 / 终结胜出。用户活动（输入恢复取消）、显式取消、故障或已完成
        // 的实例绝不再给出等效确认，也不得重复执行。
        if (instance.IsIdleRecovered)
        {
            return Denied("User activity recovered from the idle trigger; equivalent confirmation is cancelled.");
        }

        if (instance.State is TaskInstanceState.Cancelled
            or TaskInstanceState.Faulted
            or TaskInstanceState.Interrupted
            or TaskInstanceState.Executed)
        {
            return Denied("The instance is terminal; explicit cancel or recovery wins over equivalent confirmation.");
        }

        // 规则 2：授权失效 fail-closed。
        if (!authorization.IsAuthorized)
        {
            return Denied("The unattended policy is not authorized: " + authorization.Reason);
        }

        // 规则 3：仅倒计时待确认实例给出等效确认（单次、纯函数、无副作用）。
        return new UnattendedConfirmationDecision
        {
            Outcome = UnattendedConfirmationOutcome.EquivalentConfirmation,
            Reason = "Unattended equivalent confirmation granted.",
            Authorization = authorization
        };
    }

    private static UnattendedConfirmationDecision Denied(string reason)
        => new()
        {
            Outcome = UnattendedConfirmationOutcome.Denied,
            Reason = reason
        };
}
