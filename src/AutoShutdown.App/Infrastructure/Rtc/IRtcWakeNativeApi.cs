namespace AutoShutdown.App.Infrastructure.Rtc;

/// <summary>一次 Win32 原生调用的结果（S21）。失败时携带 Win32 错误码供诊断。</summary>
public readonly record struct NativeCallResult(bool Succeeded, int Win32Error)
{
    public static NativeCallResult Success() => new(true, 0);
}

/// <summary>
/// 一次性 RTC 唤醒的 Win32 原生互操作抽象（S21）。真实实现
/// <c>Win32RtcWakeNativeApi</c>（waitable timer）承载全部 P/Invoke；本接口使
/// <see cref="Win32RtcWakeService"/> 可测试（自动化测试绝不触碰真实 timer）。
/// </summary>
public interface IRtcWakeNativeApi
{
    /// <summary>创建 waitable timer；返回 IntPtr.Zero 表示失败。</summary>
    IntPtr CreateTimer();

    /// <summary>设置一次性绝对唤醒时间（FILETIME）；fResume=TRUE 支持从睡眠/休眠唤醒。</summary>
    NativeCallResult SetWake(IntPtr timer, long dueTimeFileTimeUtc);

    /// <summary>取消定时器。</summary>
    NativeCallResult Cancel(IntPtr timer);

    /// <summary>关闭定时器句柄。</summary>
    bool Close(IntPtr timer);
}
