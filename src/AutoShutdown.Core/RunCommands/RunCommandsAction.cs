using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.PrePipeline;

namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// RunCommands 动作（S19）。作为 Pre-Pipeline 的第二个 Action 注册（固定顺序
/// OfficeSave→RunCommands→CloseApps）。只消费 <see cref="RunCommandsService"/>（其内部依赖
/// 配置与 <see cref="ICommandRunner"/> 抽象）与 S16 上下文/取消令牌；不触碰电源服务边界
/// （唯一电源出口不变）、不改变任务终态。失败策略默认 Block（fail-closed）：任一 Block 策略
/// 命令失败/被拒绝即阻断关机；仅逐命令显式 Continue 才允许失败后继续。
/// </summary>
public sealed class RunCommandsAction : IPreShutdownAction
{
    private readonly RunCommandsService _service;

    public RunCommandsAction(
        RunCommandsService service,
        FailurePolicy failurePolicy = FailurePolicy.Block)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        FailurePolicy = failurePolicy;
    }

    public string Name => "RunCommands";

    public FailurePolicy FailurePolicy { get; }

    public async Task<PrePipelineActionResult> ExecuteAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await _service.RunAllAsync(cancellationToken).ConfigureAwait(false);

        return new PrePipelineActionResult
        {
            Succeeded = report.Succeeded,
            ErrorMessage = report.Succeeded ? string.Empty : report.Summary
        };
    }
}
