using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.PrePipeline;

namespace AutoShutdown.Core.Office;

/// <summary>
/// Office 自动保存动作（S17）。作为 Pre-Pipeline 的第一个 Action 注册（S17 仅此一个，
/// S18+ 追加 RunCommands、CloseApps，固定顺序 OfficeSave→RunCommands→CloseApps）。
/// 只消费 IOfficeAutomation 抽象与 S16 上下文/取消令牌；不触碰 IPowerService、
/// 不改变任务终态。失败策略默认 Continue：失败保留审计与摘要，但不阻断关机。
/// </summary>
public sealed class OfficeSaveAction : IPreShutdownAction
{
    private readonly OfficeDocumentSaver _saver;

    public OfficeSaveAction(
        IOfficeAutomation automation,
        TimeSpan? perAppTimeout = null,
        FailurePolicy failurePolicy = FailurePolicy.Continue)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _saver = new OfficeDocumentSaver(automation, perAppTimeout);
        FailurePolicy = failurePolicy;
    }

    public string Name => "OfficeSave";

    public FailurePolicy FailurePolicy { get; }

    public async Task<PrePipelineActionResult> ExecuteAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = await _saver.SaveAllAsync(cancellationToken).ConfigureAwait(false);

        return new PrePipelineActionResult
        {
            Succeeded = report.Succeeded,
            ErrorMessage = report.Succeeded ? string.Empty : report.Summary
        };
    }
}
