using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Workflow;

public sealed class ShutdownWorkflow : IShutdownWorkflow
{
    private const int CurrentSchemaVersion = 1;

    private static readonly HashSet<PowerAction> ValidActions =
        [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate];

    private readonly IConfigurationService _configurationService;
    private readonly IPowerService _powerService;
    private readonly IPrePipelineRunner _prePipelineRunner;

    public ShutdownWorkflow(
        IConfigurationService configurationService,
        IPowerService powerService,
        IPrePipelineRunner? prePipelineRunner = null)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(powerService);
        _configurationService = configurationService;
        _powerService = powerService;
        _prePipelineRunner = prePipelineRunner ?? PrePipelineRunner.Empty;
    }

    public async Task<ShutdownWorkflowResult> ExecuteAsync(
        TaskInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        AppConfig config;
        try
        {
            var load = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (load.Status != ConfigurationLoadStatus.Success
                || load.Config is null
                || load.Config.SchemaVersion != CurrentSchemaVersion)
            {
                return Reject(
                    ShutdownDecisionCode.ConfigurationUnavailable,
                    "The configuration is not available.");
            }

            config = load.Config;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Reject(
                ShutdownDecisionCode.ConfigurationUnavailable,
                "The configuration could not be loaded: " + exception.Message);
        }

        if (config.TestMode)
        {
            // 安全测试模式：保留现有模拟执行路径（由 GuardedPowerService 路由到 Fake）。
        }
        else
        {
            // 真实电源模式：闸门一为发布配置必须显式开启 RealPowerEnabled。
            if (!config.RealPowerEnabled)
            {
                return Reject(
                    ShutdownDecisionCode.RealPowerNotEnabled,
                    "Real power is not enabled in the configuration.");
            }

            // 闸门二为实例必须携带用户对本次真实执行的人工确认。
            if (!instance.RealPowerConfirmed)
            {
                return Reject(
                    ShutdownDecisionCode.RealPowerConfirmationMissing,
                    "Real power execution requires an explicit user confirmation.");
            }
        }

        if (instance.State != TaskInstanceState.Executing)
        {
            return Reject(
                ShutdownDecisionCode.InvalidState,
                "The instance is not in the Executing state.");
        }

        if (!instance.HasExecuted)
        {
            return Reject(
                ShutdownDecisionCode.ExecutionFlagMissing,
                "The instance has not been marked as executed.");
        }

        if (instance.InstanceId == Guid.Empty
            || instance.SourceTaskId == Guid.Empty
            || instance.StageToken == Guid.Empty)
        {
            return Reject(
                ShutdownDecisionCode.InvalidIdentity,
                "The instance identity fields are incomplete.");
        }

        if (!ValidActions.Contains(instance.ActionSnapshot))
        {
            return Reject(
                ShutdownDecisionCode.InvalidAction,
                $"The action {instance.ActionSnapshot} is not valid.");
        }

        if (!config.AllowedActions.Contains(instance.ActionSnapshot))
        {
            return Reject(
                ShutdownDecisionCode.ActionNotAllowed,
                $"The action {instance.ActionSnapshot} is not allowed by the configuration.");
        }

        if (instance.CreatedAt == default
            || instance.ScheduledFireTime == default
            || instance.ScheduledFireTime < instance.CreatedAt)
        {
            return Reject(
                ShutdownDecisionCode.InvalidTiming,
                "The instance timing fields are invalid.");
        }

        // 确认已成功（双闸门 + 状态/身份/动作/时序校验全部通过）后才运行 Pre-Pipeline。
        // 用户取消（CancellationToken）在此前已由调度/上层拦截，此处不再产生副作用。
        var pipeline = await _prePipelineRunner
            .RunAsync(
                new PrePipelineContext
                {
                    InstanceId = instance.InstanceId,
                    SourceTaskId = instance.SourceTaskId,
                    Action = instance.ActionSnapshot,
                    ScheduledFireTime = instance.ScheduledFireTime
                },
                cancellationToken).ConfigureAwait(false);

        if (!pipeline.PowerAllowed)
        {
            return new ShutdownWorkflowResult
            {
                Status = ShutdownWorkflowStatus.Rejected,
                DecisionCode = ShutdownDecisionCode.PrePipelineBlocked,
                PrePipeline = pipeline,
                Message = "The pre-pipeline blocked the power action."
            };
        }

        var request = new PowerRequest
        {
            Action = instance.ActionSnapshot,
            InstanceId = instance.InstanceId,
            Reason = "The scheduled task is due and has been approved for execution.",
            RealPowerConfirmed = instance.RealPowerConfirmed
        };

        PowerResult powerResult;
        try
        {
            powerResult = await _powerService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ShutdownWorkflowResult
            {
                Status = ShutdownWorkflowStatus.PowerFailed,
                DecisionCode = ShutdownDecisionCode.PowerServiceException,
                PrePipeline = pipeline,
                Message = "The power service threw an exception: " + exception.Message
            };
        }

        if (powerResult.Outcome == PowerOutcome.Simulated && powerResult.WasSimulated)
        {
            return new ShutdownWorkflowResult
            {
                Status = ShutdownWorkflowStatus.Simulated,
                DecisionCode = ShutdownDecisionCode.Allowed,
                PowerResult = powerResult,
                PrePipeline = pipeline,
                Message = "The power action was simulated successfully."
            };
        }

        if (powerResult.Outcome == PowerOutcome.Accepted)
        {
            return new ShutdownWorkflowResult
            {
                Status = ShutdownWorkflowStatus.Accepted,
                DecisionCode = ShutdownDecisionCode.Allowed,
                PowerResult = powerResult,
                PrePipeline = pipeline,
                Message = "The real power action was accepted."
            };
        }

        if (powerResult.Outcome == PowerOutcome.Rejected)
        {
            return new ShutdownWorkflowResult
            {
                Status = ShutdownWorkflowStatus.Rejected,
                DecisionCode = ShutdownDecisionCode.PowerServiceRejected,
                PowerResult = powerResult,
                PrePipeline = pipeline,
                Message = "The power service rejected the request."
            };
        }

        if (powerResult.Outcome == PowerOutcome.Failed)
        {
            return new ShutdownWorkflowResult
            {
                Status = ShutdownWorkflowStatus.PowerFailed,
                DecisionCode = ShutdownDecisionCode.PowerServiceFailed,
                PowerResult = powerResult,
                PrePipeline = pipeline,
                Message = "The power service reported a failure."
            };
        }

        return new ShutdownWorkflowResult
        {
            Status = ShutdownWorkflowStatus.PowerFailed,
            DecisionCode = ShutdownDecisionCode.PowerServiceFailed,
            PowerResult = powerResult,
            PrePipeline = pipeline,
            Message = "The power service returned an unexpected result."
        };
    }

    private static ShutdownWorkflowResult Reject(
        ShutdownDecisionCode decisionCode,
        string message) => new()
        {
            Status = ShutdownWorkflowStatus.Rejected,
            DecisionCode = decisionCode,
            Message = message
        };
}
