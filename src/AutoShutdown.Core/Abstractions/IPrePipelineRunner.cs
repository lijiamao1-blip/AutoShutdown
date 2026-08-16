using AutoShutdown.Core.PrePipeline;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// Pre-Pipeline 串行执行器（S16）。按注册顺序串行执行动作，
/// 记录开始/耗时/结果/脱敏错误；block 立即停止并取消电源意图，
/// continue 继续但保留失败审计。
/// </summary>
public interface IPrePipelineRunner
{
    Task<PrePipelineRunResult> RunAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken);
}
