using AutoShutdown.Core.Rtc;

namespace AutoShutdown.App.Infrastructure.Rtc;

/// <summary>
/// Windows 一次性 RTC 唤醒实现（S21）。基于 waitable timer（<see cref="IRtcWakeNativeApi"/>，
/// 真实 P/Invoke 收敛于 Win32PowerNativeApi.cs）。能力诚实声明：可从睡眠（S3）/休眠（S4）
/// 唤醒，但无法从完全关机（S5）唤醒——因为 waitable timer 是进程内定时器，进程在关机时消亡。
/// 权限不足（SE_INCREASE_QUOTA_NAME 缺失 → ERROR_PRIVILEGE_NOT_HELD/ACCESS_DENIED）明确返回
/// NotAuthorized；系统拒绝明确返回 FirmwareRejected；取消直接传播。绝不安装驱动、改写固件或
/// 更改系统电源策略。作为单例持有当前武装的定时器句柄；ClearAsync 幂等。
/// </summary>
public sealed class Win32RtcWakeService : IRtcWakeService
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorPrivilegeNotHeld = 1314; // ERROR_PRIVILEGE_NOT_HELD

    private readonly IRtcWakeNativeApi _native;
    private IntPtr _timer = IntPtr.Zero;

    public Win32RtcWakeService(IRtcWakeNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public Task<RtcWakeCapabilityResult> GetCapabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new RtcWakeCapabilityResult
        {
            Status = RtcWakeCapabilityStatus.Supported,
            WakeScope = RtcWakeScope.Suspend,
            Reason =
                "Windows waitable timers (SetWaitableTimer with fResume=TRUE) can wake the system from "
                + "sleep (S3) and hibernate (S4), but they do not survive a full shutdown (S5); wake "
                + "from S5 requires firmware RTC alarm support and is not provided by this service."
        });
    }

    public Task<RtcWakeSetResult> SetWakeAsync(
        DateTimeOffset wakeTimeUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 一次性唤醒：先取消/关闭旧的定时器（若有），再创建新的。
        if (_timer != IntPtr.Zero)
        {
            _native.Cancel(_timer);
            _native.Close(_timer);
            _timer = IntPtr.Zero;
        }

        var handle = _native.CreateTimer();
        if (handle == IntPtr.Zero)
        {
            return FromSetFailure(
                RtcWakeSetStatus.FirmwareRejected,
                "CreateWaitableTimerExW failed; the system rejected the wake timer.");
        }

        // 绝对时间（FILETIME：自 1601-01-01 起 100ns 计数），与 SetWaitableTimer 正数语义一致。
        var dueTime = wakeTimeUtc.ToFileTime();
        var set = _native.SetWake(handle, dueTime);
        if (!set.Succeeded)
        {
            _native.Close(handle);
            var status = set.Win32Error is ErrorPrivilegeNotHeld or ErrorAccessDenied
                ? RtcWakeSetStatus.NotAuthorized
                : RtcWakeSetStatus.FirmwareRejected;
            return FromSetFailure(status, $"SetWaitableTimer failed with Win32 error {set.Win32Error}.");
        }

        _timer = handle;
        return Task.FromResult(new RtcWakeSetResult
        {
            Status = RtcWakeSetStatus.Success,
            Message = "The one-shot RTC wake was armed."
        });
    }

    public Task<RtcWakeClearResult> ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_timer == IntPtr.Zero)
        {
            return Task.FromResult(new RtcWakeClearResult
            {
                Status = RtcWakeClearStatus.Success,
                Message = "No pending RTC wake timer."
            });
        }

        var cancel = _native.Cancel(_timer);
        var close = _native.Close(_timer);
        _timer = IntPtr.Zero;

        if (!cancel.Succeeded || !close)
        {
            return Task.FromResult(new RtcWakeClearResult
            {
                Status = RtcWakeClearStatus.CleanupFailed,
                Message = $"CancelWaitableTimer/CloseHandle failed (Win32 error {cancel.Win32Error})."
            });
        }

        return Task.FromResult(new RtcWakeClearResult
        {
            Status = RtcWakeClearStatus.Success,
            Message = "The RTC wake timer was cleared."
        });
    }

    private static Task<RtcWakeSetResult> FromSetFailure(RtcWakeSetStatus status, string message)
        => Task.FromResult(new RtcWakeSetResult
        {
            Status = status,
            Message = message
        });
}
