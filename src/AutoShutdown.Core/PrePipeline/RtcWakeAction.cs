using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Rtc;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.PrePipeline;

/// <summary>
/// 一次性 RTC 唤醒动作（S21）。作为 Pre-Pipeline 的最后一个 Action 注册（固定顺序
/// OfficeSave→RunCommands→CloseApps→RtcWake）。仅在任务携带唤醒时间且非测试模式时武装
/// 一次性 RTC 唤醒；取消直接传播（不产生副作用）；Unsupported/权限不足/固件拒绝一律
/// 明确失败，绝不伪造设置成功。动作只消费 <see cref="IRtcWakeService"/>（抽象）与
/// 配置/上下文/取消令牌，不触碰电源服务边界（唯一电源出口不变），不改变任务终态。
/// </summary>
public sealed class RtcWakeAction : IPreShutdownAction
{
    public const string ActionName = "RtcWake";

    private readonly IRtcWakeService _rtcWakeService;
    private readonly IConfigurationService _configurationService;

    public RtcWakeAction(
        IRtcWakeService rtcWakeService,
        IConfigurationService configurationService,
        FailurePolicy failurePolicy = FailurePolicy.Block)
    {
        ArgumentNullException.ThrowIfNull(rtcWakeService);
        ArgumentNullException.ThrowIfNull(configurationService);
        _rtcWakeService = rtcWakeService;
        _configurationService = configurationService;
        FailurePolicy = failurePolicy;
    }

    public string Name => ActionName;

    public FailurePolicy FailurePolicy { get; }

    public async Task<PrePipelineActionResult> ExecuteAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 未配置唤醒时间 → 无操作成功（C2 阶段任务尚不携带唤醒时间；C3 接线后生效）。
        if (context.RtcWakeTimeUtc is not { } wakeTime)
        {
            return new PrePipelineActionResult { Succeeded = true };
        }

        // 安全测试模式：不触碰真实 timer（自动化测试绝不武装真实 RTC 唤醒）。
        var load = await _configurationService
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (load.Status != ConfigurationLoadStatus.Success || load.Config is null)
        {
            return new PrePipelineActionResult
            {
                Succeeded = false,
                ErrorMessage = "The configuration could not be loaded; the RTC wake was not armed."
            };
        }

        if (load.Config.TestMode)
        {
            return new PrePipelineActionResult { Succeeded = true };
        }

        RtcWakeCapabilityResult capability;
        try
        {
            capability = await _rtcWakeService
                .GetCapabilityAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PrePipelineActionResult
            {
                Succeeded = false,
                ErrorMessage = "The RTC wake capability could not be determined: " + exception.Message
            };
        }

        if (!capability.Supported)
        {
            return new PrePipelineActionResult
            {
                Succeeded = false,
                ErrorMessage = "One-shot RTC wake is not supported: " + capability.Reason
            };
        }

        // 唤醒范围必须覆盖本次动作的电源状态：完全关机（S5）需要更强的能力。
        var requiredScope = RequiredWakeScope(context.Action);
        if (requiredScope > capability.WakeScope)
        {
            return new PrePipelineActionResult
            {
                Succeeded = false,
                ErrorMessage =
                    $"One-shot RTC wake cannot wake this machine from {context.Action}: {capability.Reason}"
            };
        }

        RtcWakeSetResult set;
        try
        {
            set = await _rtcWakeService
                .SetWakeAsync(wakeTime, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PrePipelineActionResult
            {
                Succeeded = false,
                ErrorMessage = "The RTC wake could not be armed: " + exception.Message
            };
        }

        switch (set.Status)
        {
            case RtcWakeSetStatus.Success:
                return new PrePipelineActionResult { Succeeded = true };
            case RtcWakeSetStatus.Unsupported:
                return Failure("One-shot RTC wake is not supported for this scenario: " + set.Message);
            case RtcWakeSetStatus.NotAuthorized:
                return Failure("The RTC wake was not permitted: " + set.Message);
            case RtcWakeSetStatus.FirmwareRejected:
                return Failure("The system rejected the RTC wake: " + set.Message);
            case RtcWakeSetStatus.Cancelled:
                throw new OperationCanceledException(set.Message);
            default:
                return Failure("The RTC wake service returned an unknown result.");
        }
    }

    private static PrePipelineActionResult Failure(string message) => new()
    {
        Succeeded = false,
        ErrorMessage = message
    };

    /// <summary>
    /// 动作所需的唤醒范围（S21）：睡眠/休眠只需 Suspend；完全关机/重启（以及无法识别
    /// 的动作）按 fail-closed 要求最强的 SuspendAndPowerOff。
    /// </summary>
    private static RtcWakeScope RequiredWakeScope(PowerAction action) => action switch
    {
        PowerAction.Sleep or PowerAction.Hibernate => RtcWakeScope.Suspend,
        _ => RtcWakeScope.SuspendAndPowerOff
    };
}
