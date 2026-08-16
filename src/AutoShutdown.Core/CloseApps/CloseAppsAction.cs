using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.PrePipeline;

namespace AutoShutdown.Core.CloseApps;

/// <summary>
/// CloseApps 动作（S18）。作为 Pre-Pipeline 的最后一个 Action 注册（固定顺序
/// OfficeSave→RunCommands→CloseApps）。只消费 <see cref="CloseAppsService"/>（其内部依赖
/// 进程/窗口/配置抽象）与 S16 上下文/取消令牌；不触碰电源服务边界（唯一电源出口不变）、
/// 不改变任务终态。失败策略默认 Block（fail-closed）：关闭应用失败（超时/访问拒绝/无窗口等）
/// 阻断关机，绝不静默继续；仅显式 Continue 才允许失败后继续。
/// </summary>
public sealed class CloseAppsAction : IPreShutdownAction
{
    private readonly CloseAppsService _service;

    public CloseAppsAction(
        CloseAppsService service,
        FailurePolicy failurePolicy = FailurePolicy.Block)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        FailurePolicy = failurePolicy;
    }

    public string Name => "CloseApps";

    public FailurePolicy FailurePolicy { get; }

    public async Task<PrePipelineActionResult> ExecuteAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await _service.CloseAllAsync(cancellationToken).ConfigureAwait(false);

        return new PrePipelineActionResult
        {
            Succeeded = report.Succeeded,
            ErrorMessage = report.Succeeded ? string.Empty : report.Summary
        };
    }
}
