namespace AutoShutdown.Core.Rtc;

/// <summary>
/// 一次性 RTC 唤醒服务（S21）。平台无关的能力/设置/清除抽象：
/// <list type="bullet">
///   <item><see cref="GetCapabilityAsync"/>——平台是否支持一次性 RTC 唤醒，及唤醒范围（诚实、可诊断）；</item>
///   <item><see cref="SetWakeAsync"/>——武装一次性唤醒；Unsupported/权限不足/固件拒绝/取消一律明确失败，绝不伪造成功；</item>
///   <item><see cref="ClearAsync"/>——清除已武装的唤醒；清除失败明确上报（CleanupFailed）。</item>
/// </list>
/// 本服务只作为受控的关机前步骤（RTC 唤醒仅随本次关机/睡眠/休眠流程一次性设置），
/// 不提供独立定时唤醒。实现不得安装驱动、改写固件或更改系统电源策略。
/// </summary>
public interface IRtcWakeService
{
    Task<RtcWakeCapabilityResult> GetCapabilityAsync(CancellationToken cancellationToken);

    Task<RtcWakeSetResult> SetWakeAsync(
        DateTimeOffset wakeTimeUtc,
        CancellationToken cancellationToken);

    Task<RtcWakeClearResult> ClearAsync(CancellationToken cancellationToken);
}
