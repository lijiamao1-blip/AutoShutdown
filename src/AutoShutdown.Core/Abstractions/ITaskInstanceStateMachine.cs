using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// V2 8 态任务实例状态机接口。
/// 纯逻辑：TryTransition 只返回结构化结果，不写日志；
/// 调用方（调度/工作流等）负责对拒绝结果记录 Error 审计日志。
/// </summary>
public interface ITaskInstanceStateMachine
{
    TaskInstanceStateTransitionResult TryTransition(
        TaskInstanceState current,
        TaskInstanceState target,
        TaskInstanceStateTransitionCause cause,
        string? source = null);
}
