using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;

namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// Decorates <see cref="ITaskArbitrator"/> with audit logging only. The arbitration
/// decision passes through unchanged; every decision records an audit entry carrying
/// WinnerTaskId / MergedTaskIds / RescheduledTaskIds / RequiresUserDecision / DecisionReason,
/// so multi-task due-time arbitration is auditable. RequiresUserDecision decisions are
/// recorded at Warning level (user attention required), the rest at Information level.
/// </summary>
public sealed class LoggingTaskArbitratorDecorator : ITaskArbitrator
{
    private readonly ITaskArbitrator _inner;
    private readonly IApplicationLogger _logger;

    public LoggingTaskArbitratorDecorator(
        ITaskArbitrator inner,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _logger = logger;
    }

    public TaskArbitrationResult Arbitrate(
        IReadOnlyList<TaskInstance> dueInstances,
        DateTimeOffset now)
    {
        var result = _inner.Arbitrate(dueInstances, now);

        var message =
            "仲裁决策：" + result.DecisionReason
            + "，获胜：" + (result.WinnerTaskId?.ToString() ?? "无")
            + "，合并：" + string.Join(",", result.MergedTaskIds)
            + "，改期：" + string.Join(",", result.RescheduledTaskIds)
            + "，需人工决策：" + result.RequiresUserDecision;

        if (result.RequiresUserDecision)
        {
            _logger.Warning("TaskArbitrationNeedsUserDecision", message);
        }
        else
        {
            _logger.Info("TaskArbitrated", message);
        }

        return result;
    }
}
