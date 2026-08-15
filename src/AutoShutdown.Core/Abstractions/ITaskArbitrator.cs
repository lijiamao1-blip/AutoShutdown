using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 多任务同时到期的仲裁接口（S13-T04 定义 + 调用点；正式实现归 T06）。
/// 引擎在同一时刻检测到多个实例到期时调用本接口决策赢家 / 合并 / 重排。
/// 纯逻辑：不写日志、不写状态；引擎依据返回结果推进。
/// </summary>
public interface ITaskArbitrator
{
    /// <summary>
    /// 对同时到期的一组实例作出裁决。
    /// </summary>
    /// <param name="dueInstances">当前同时到期、等待裁决的实例（≥2）。</param>
    /// <param name="now">裁决时间点（UTC）。</param>
    TaskArbitrationResult Arbitrate(
        IReadOnlyList<TaskInstance> dueInstances,
        DateTimeOffset now);
}
