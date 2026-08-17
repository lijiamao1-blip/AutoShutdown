using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Unattended;

namespace AutoShutdown.Core.Workflow;

public sealed class ShutdownWorkflow : IShutdownWorkflow
{
    private const int CurrentSchemaVersion = 1;

    private static readonly HashSet<PowerAction> ValidActions =
        [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate];

    private readonly IConfigurationService _configurationService;
    private readonly IPowerService _powerService;
    private readonly IPrePipelineRunner _prePipelineRunner;
    private readonly IUnattendedPolicyService? _unattendedPolicyService;
    private readonly UnattendedConfirmationEvaluator? _unattendedEvaluator;

    public ShutdownWorkflow(
        IConfigurationService configurationService,
        IPowerService powerService,
        IPrePipelineRunner? prePipelineRunner = null,
        IUnattendedPolicyService? unattendedPolicyService = null,
        UnattendedConfirmationEvaluator? unattendedEvaluator = null)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(powerService);
        _configurationService = configurationService;
        _powerService = powerService;
        _prePipelineRunner = prePipelineRunner ?? PrePipelineRunner.Empty;
        _unattendedPolicyService = unattendedPolicyService;
        _unattendedEvaluator = unattendedEvaluator;
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

        // 无人值守等效确认（S20）：仅真实电源路径可能命中；人工确认或测试模式时为 null。
        UnattendedConfirmationDecision? unattendedConfirmation = null;

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

            // 闸门二：真实电源必须携带人工确认；无人值守等效确认仅在授权有效且实例未终结时
            // 替代人工确认（S20）。授权失效/实例终结/评估异常一律 fail-closed 拒绝。
            var confirmation = await ResolveRealPowerConfirmationAsync(instance, cancellationToken)
                .ConfigureAwait(false);
            if (!confirmation.Allowed)
            {
                return Reject(confirmation.DecisionCode, confirmation.Message);
            }

            unattendedConfirmation = confirmation.UnattendedConfirmation;
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
                UnattendedConfirmation = unattendedConfirmation,
                Message = "The pre-pipeline blocked the power action."
            };
        }

        // 等效确认（无人值守授权）与人工确认等价地满足双闸门之二；
        // GuardedPowerService 仅见 effectiveConfirmed=true，无需感知确认来源。
        var effectiveConfirmed = instance.RealPowerConfirmed
            || unattendedConfirmation is { AllowsPower: true };

        var request = new PowerRequest
        {
            Action = instance.ActionSnapshot,
            InstanceId = instance.InstanceId,
            Reason = unattendedConfirmation is { AllowsPower: true }
                ? "The scheduled task is due and unattended equivalent confirmation was granted."
                : "The scheduled task is due and has been approved for execution.",
            RealPowerConfirmed = effectiveConfirmed
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
                UnattendedConfirmation = unattendedConfirmation,
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
                UnattendedConfirmation = unattendedConfirmation,
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
                UnattendedConfirmation = unattendedConfirmation,
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
                UnattendedConfirmation = unattendedConfirmation,
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
                UnattendedConfirmation = unattendedConfirmation,
                Message = "The power service reported a failure."
            };
        }

        return new ShutdownWorkflowResult
        {
            Status = ShutdownWorkflowStatus.PowerFailed,
            DecisionCode = ShutdownDecisionCode.PowerServiceFailed,
            PowerResult = powerResult,
            PrePipeline = pipeline,
            UnattendedConfirmation = unattendedConfirmation,
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

    /// <summary>
    /// 解析真实电源的双闸门之二（S20）：人工确认优先；无人值守等效确认仅在其授权有效
    /// 且实例未终结时替代人工确认。授权失效/评估异常一律 fail-closed。
    /// </summary>
    private async Task<RealPowerConfirmationResolution> ResolveRealPowerConfirmationAsync(
        TaskInstance instance,
        CancellationToken cancellationToken)
    {
        if (instance.RealPowerConfirmed)
        {
            return new RealPowerConfirmationResolution
            {
                Allowed = true,
                DecisionCode = ShutdownDecisionCode.Allowed
            };
        }

        // S20-D1：无人值守等效确认仅对显式选择 UseUnattended 的任务生效。未选择（默认关闭）
        // 且无人工确认时拒绝，不得把全局无人值守授权自动应用到所有任务（不默认跳过人工确认）。
        if (!instance.UseUnattended)
        {
            return new RealPowerConfirmationResolution
            {
                Allowed = false,
                DecisionCode = ShutdownDecisionCode.RealPowerConfirmationMissing,
                Message = "Real power execution requires an explicit user confirmation."
            };
        }

        if (_unattendedPolicyService is null || _unattendedEvaluator is null)
        {
            return new RealPowerConfirmationResolution
            {
                Allowed = false,
                DecisionCode = ShutdownDecisionCode.RealPowerConfirmationMissing,
                Message = "Real power execution requires an explicit user confirmation."
            };
        }

        UnattendedAuthorizationDecision authorization;
        try
        {
            authorization = await _unattendedPolicyService
                .EvaluateAsync(instance.ActionSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RealPowerConfirmationResolution
            {
                Allowed = false,
                DecisionCode = ShutdownDecisionCode.UnattendedNotAuthorized,
                Message = "The unattended policy could not be evaluated: " + exception.Message
            };
        }

        var decision = _unattendedEvaluator.Evaluate(authorization, instance);
        if (decision.AllowsPower)
        {
            return new RealPowerConfirmationResolution
            {
                Allowed = true,
                DecisionCode = ShutdownDecisionCode.Allowed,
                UnattendedConfirmation = decision
            };
        }

        return new RealPowerConfirmationResolution
        {
            Allowed = false,
            DecisionCode = ShutdownDecisionCode.UnattendedNotAuthorized,
            UnattendedConfirmation = decision,
            Message = "Unattended equivalent confirmation was denied: " + decision.Reason
        };
    }

    private sealed record RealPowerConfirmationResolution
    {
        public bool Allowed { get; init; }

        public ShutdownDecisionCode DecisionCode { get; init; } = ShutdownDecisionCode.Unknown;

        public UnattendedConfirmationDecision? UnattendedConfirmation { get; init; }

        public string Message { get; init; } = string.Empty;
    }
}
