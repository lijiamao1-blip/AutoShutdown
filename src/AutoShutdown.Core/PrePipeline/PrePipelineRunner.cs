using System.Diagnostics;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.PrePipeline;

/// <summary>
/// Pre-Pipeline 串行执行器（S16）。按注册顺序串行执行 <see cref="IPreShutdownAction"/>；
/// 每个动作的异常被捕获并转成 <see cref="PrePipelineActionResult"/>（脱敏错误），
/// 单个扩展异常不会使进程失控。block 失败立即停止并取消电源意图；
/// continue 失败记录审计后继续。取消（CancellationToken）直接传播，不产生副作用。
/// </summary>
public sealed class PrePipelineRunner : IPrePipelineRunner
{
    private const int MaxErrorMessageLength = 200;

    private readonly IReadOnlyList<IPreShutdownAction> _actions;

    public PrePipelineRunner(IEnumerable<IPreShutdownAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        // 保留注册顺序：框架拥有顺序（S17+ 固定为 OfficeSave→RunCommands→CloseApps）。
        _actions = actions.ToArray();
    }

    /// <summary>无动作的空流水线（S16 生产默认：三个真实 Action 尚未实现）。</summary>
    public static PrePipelineRunner Empty { get; } = new(Array.Empty<IPreShutdownAction>());

    public async Task<PrePipelineRunResult> RunAsync(
        PrePipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<PrePipelineActionResult>(_actions.Count);

        foreach (var action in _actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            PrePipelineActionResult result;
            try
            {
                result = await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false)
                    ?? new PrePipelineActionResult { Succeeded = false, ErrorMessage = "The action returned no result." };
            }
            catch (OperationCanceledException)
            {
                throw; // 取消不是失败：直接传播，不产生副作用。
            }
            catch (Exception exception)
            {
                // Runner 捕获每个动作异常并转成 ActionResult，不因单个扩展异常失控。
                result = new PrePipelineActionResult
                {
                    Succeeded = false,
                    ErrorMessage = exception.Message
                };
            }

            stopwatch.Stop();
            var recorded = result with
            {
                ActionName = action.Name,
                FailurePolicy = action.FailurePolicy,
                StartedAtUtc = startedAt,
                Duration = stopwatch.Elapsed,
                // 安全收敛点（S16 独立验收修复）：无论失败来自异常、null 返回还是
                // Action 主动返回，最终记录的失败 ErrorMessage 都经同一 SanitizeError
                // （折叠控制字符、限长 200）；成功结果一律清空 ErrorMessage，
                // 不记录命令参数、文档内容或凭据。
                ErrorMessage = result.Succeeded ? string.Empty : SanitizeError(result.ErrorMessage)
            };
            results.Add(recorded);

            // 安全契约（S16 独立验收修复）：仅明确 FailurePolicy.Continue 允许失败后继续；
            // Block 阻断；Unknown 或任何未定义枚举值一律 fail-closed（按 Block 处理），
            // 绝不把 Unknown 静默转换为 Continue。
            if (!recorded.Succeeded && recorded.FailurePolicy != FailurePolicy.Continue)
            {
                return new PrePipelineRunResult
                {
                    Status = PrePipelineRunStatus.Blocked,
                    Actions = results
                };
            }
        }

        return new PrePipelineRunResult
        {
            Status = PrePipelineRunStatus.Completed,
            Actions = results
        };
    }

    /// <summary>
    /// 脱敏错误文本：折叠控制字符（防日志注入）并截断到安全长度。
    /// 真实 Action（S17+）不得在异常中携带命令参数/文档内容/凭据；
    /// 此方法为错误文本进入日志前的唯一收敛点。
    /// </summary>
    private static string SanitizeError(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var sanitized = new string(message
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());

        return sanitized.Length <= MaxErrorMessageLength
            ? sanitized
            : sanitized[..MaxErrorMessageLength];
    }
}
