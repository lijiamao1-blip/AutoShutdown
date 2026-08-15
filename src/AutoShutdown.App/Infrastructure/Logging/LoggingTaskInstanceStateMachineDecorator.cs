using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// Decorates ITaskInstanceStateMachine with audit logging only. Allowed transitions
/// pass through untouched; rejected transitions record an Error audit log carrying the
/// stable DecisionCode, source, states, cause and reason, then pass the result through
/// unchanged so callers keep their existing rejection handling.
/// </summary>
public sealed class LoggingTaskInstanceStateMachineDecorator : ITaskInstanceStateMachine
{
    private readonly ITaskInstanceStateMachine _inner;
    private readonly IApplicationLogger _logger;

    public LoggingTaskInstanceStateMachineDecorator(
        ITaskInstanceStateMachine inner,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _logger = logger;
    }

    public TaskInstanceStateTransitionResult TryTransition(
        TaskInstanceState current,
        TaskInstanceState target,
        TaskInstanceStateTransitionCause cause,
        string? source = null)
    {
        var result = _inner.TryTransition(current, target, cause, source);

        if (!result.Allowed)
        {
            _logger.Error(
                "StateTransitionRejected",
                "非法状态转换被拒绝，来源：" + source
                    + "，当前态：" + result.CurrentState
                    + "，目标态：" + result.TargetState
                    + "，原因：" + result.Cause
                    + "，决策码：" + result.DecisionCode
                    + "，说明：" + result.Reason);
        }

        return result;
    }
}
