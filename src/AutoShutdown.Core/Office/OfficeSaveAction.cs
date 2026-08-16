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
    private readonly FailurePolicy _configuredPolicy;
    private bool _helperCleanupFailed;

    public OfficeSaveAction(
        IOfficeAutomation automation,
        TimeSpan? perAppTimeout = null,
        FailurePolicy failurePolicy = FailurePolicy.Continue)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _saver = new OfficeDocumentSaver(automation, perAppTimeout);
        _configuredPolicy = failurePolicy;
    }

    public string Name => "OfficeSave";

    /// <summary>
    /// 默认 <see cref="_configuredPolicy"/>（Continue）。仅当本次运行发生「Helper 未确认退出」
    /// 安全故障（<see cref="OfficeHelperCleanupFailedException"/>）时 fail-closed 为 Block
    /// （S17 独立验收 D4）；普通 Office 保存失败仍保持 Continue。Runner 在动作返回后读取本属性
    /// 决定 block/continue，故本覆盖不改变 Runner 通用语义。
    /// </summary>
    public FailurePolicy FailurePolicy =>
        _helperCleanupFailed ? FailurePolicy.Block : _configuredPolicy;

    public async Task<PrePipelineActionResult> ExecuteAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        OfficeSaveReport report;
        try
        {
            report = await _saver.SaveAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OfficeHelperCleanupFailedException exception)
        {
            // D4：内部硬超时期间无法确认 Helper 退出——安全故障，fail-closed（Block）。
            // 仅此异常覆盖默认 Continue；普通 Office 保存失败仍 Continue。
            _helperCleanupFailed = true;
            return new PrePipelineActionResult
            {
                Succeeded = false,
                ErrorMessage = exception.Message
            };
        }

        return new PrePipelineActionResult
        {
            Succeeded = report.Succeeded,
            ErrorMessage = report.Succeeded ? string.Empty : report.Summary
        };
    }
}
