using AutoShutdown.Core.PrePipeline;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// Pre-Pipeline 动作（S16）。只返回结果，不直接调电源、不自行改变任务终态。
/// 注册顺序即执行顺序（框架拥有顺序；S17+ 固定为 OfficeSave→RunCommands→CloseApps）。
/// </summary>
public interface IPreShutdownAction
{
    string Name { get; }

    FailurePolicy FailurePolicy { get; }

    Task<PrePipelineActionResult> ExecuteAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken);
}
