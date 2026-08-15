using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;

namespace AutoShutdown.App.Infrastructure.Logging;

/// <summary>
/// Decorates IShutdownWorkflow with logging only. Parameters, return values,
/// exceptions and business order are passed through untouched. Safe denials
/// always record their stable DecisionCode. Under the current Fake power
/// service the log text never claims a real power action succeeded.
/// </summary>
public sealed class LoggingShutdownWorkflowDecorator : IShutdownWorkflow
{
    private readonly IShutdownWorkflow _inner;
    private readonly IApplicationLogger _logger;

    public LoggingShutdownWorkflowDecorator(
        IShutdownWorkflow inner,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _logger = logger;
    }

    public async Task<ShutdownWorkflowResult> ExecuteAsync(
        TaskInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _logger.Info(
            "ShutdownWorkflowStarted",
            "执行关机工作流，动作：" + instance.ActionSnapshot);

        ShutdownWorkflowResult result;
        try
        {
            result = await _inner.ExecuteAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Cancellation is not a logging concern; propagate unchanged.
        }
        catch (Exception exception)
        {
            _logger.Error("PowerServiceFailed", "电源执行抛出异常", exception);
            throw; // The original exception must propagate with its original semantics.
        }

        LogResult(result);
        return result;
    }

    private void LogResult(ShutdownWorkflowResult result)
    {
        switch (result.Status)
        {
            case ShutdownWorkflowStatus.Simulated:
                _logger.Info(
                    "ShutdownWorkflowCompleted",
                    "工作流完成，决策：" + result.DecisionCode);
                _logger.Info(
                    "PowerRequestSimulated",
                    "模拟电源请求已接受（当前为安全测试模式）。");
                break;

            case ShutdownWorkflowStatus.Accepted:
                _logger.Info(
                    "ShutdownWorkflowCompleted",
                    "工作流完成，决策：" + result.DecisionCode);
                _logger.Info(
                    "RealPowerSucceeded",
                    "真实电源动作已被接受执行，DecisionCode=" + result.DecisionCode);
                break;

            case ShutdownWorkflowStatus.Rejected:
                _logger.Warning(
                    "ShutdownWorkflowRejected",
                    "工作流安全拒绝，DecisionCode=" + result.DecisionCode
                        + "，原因：" + result.Message);
                if (result.DecisionCode == ShutdownDecisionCode.PowerServiceRejected)
                {
                    _logger.Warning(
                        "PowerServiceRejected",
                        "电源服务拒绝了请求，DecisionCode=" + result.DecisionCode);
                }

                break;

            default:
                _logger.Error(
                    "PowerServiceFailed",
                    "电源执行失败，DecisionCode=" + result.DecisionCode
                        + "，原因：" + result.Message);
                break;
        }
    }
}
