using System.Runtime.InteropServices;
using AutoShutdown.App.Infrastructure.Rtc;

namespace AutoShutdown.App.Infrastructure.Power;

/// <summary>
/// 真实 Windows 电源 API 的唯一实现。本文件是全代码库唯一允许包含 P/Invoke 的位置
/// （冻结安全契约：DllImport 只允许存在于本文件），承载：
/// <see cref="ExitWindowsEx"/>（关机/重启）、<see cref="SetSuspendState"/>（睡眠/休眠）、
/// 获取 SE_SHUTDOWN_NAME 权限所需的 token 操作，以及 S15 空闲检测的
/// <see cref="GetLastInputInfo"/>（见 <see cref="Win32IdleNativeApi"/>）。
/// 禁止在本文件之外出现任何真实电源调用或 DllImport；禁止通过系统命令行、脚本或进程启动方式执行电源操作。
/// </summary>
public sealed class Win32PowerNativeApi : IPowerNativeApi
{
    // ExitWindowsEx flags
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_REBOOT = 0x00000002;
    private const uint EWX_POWEROFF = 0x00000008;
    private const uint EWX_FORCEIFHUNG = 0x00000010;

    // SetSuspendState 参数
    private const bool SleepMode = false;    // 睡眠
    private const bool HibernateMode = true; // 休眠

    // Token privileges
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    // AdjustTokenPrivileges 的「部分成功」语义：调用本身可以成功（返回 TRUE），
    // 同时因为账户并不持有请求的权限而什么都没启用，此时 LastError = ERROR_NOT_ALL_ASSIGNED。
    private const int ErrorSuccess = 0;

    public (bool Succeeded, int? NativeErrorCode) Shutdown(bool forceIfHung = false)
    {
        if (!EnableShutdownPrivilege(out var privilegeError))
        {
            return (false, privilegeError);
        }

        var flags = EWX_SHUTDOWN | EWX_POWEROFF | (forceIfHung ? EWX_FORCEIFHUNG : 0);
        var ok = ExitWindowsEx(flags, 0);
        return ok ? (true, null) : (false, Marshal.GetLastWin32Error());
    }

    public (bool Succeeded, int? NativeErrorCode) Restart(bool forceIfHung = false)
    {
        if (!EnableShutdownPrivilege(out var privilegeError))
        {
            return (false, privilegeError);
        }

        var flags = EWX_REBOOT | (forceIfHung ? EWX_FORCEIFHUNG : 0);
        var ok = ExitWindowsEx(flags, 0);
        return ok ? (true, null) : (false, Marshal.GetLastWin32Error());
    }

    public (bool Succeeded, int? NativeErrorCode) Sleep()
    {
        var ok = SetSuspendState(SleepMode, false, false);
        return ok ? (true, null) : (false, Marshal.GetLastWin32Error());
    }

    public (bool Succeeded, int? NativeErrorCode) Hibernate()
    {
        var ok = SetSuspendState(HibernateMode, false, false);
        return ok ? (true, null) : (false, Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// 为关机/重启获取 SE_SHUTDOWN_NAME 权限。成功返回 true；失败返回 false，并经
    /// <paramref name="nativeErrorCode"/> 交回**发生在权限环节**的原生错误码。
    ///
    /// 关键点（S-POWER-D1）：<c>AdjustTokenPrivileges</c> 的返回值只表示「调用本身没出错」。
    /// 当账户实际不持有 SeShutdownPrivilege 时它**仍然返回 TRUE**，只是把 LastError 置为
    /// ERROR_NOT_ALL_ASSIGNED(1300) —— 这是该 API 的既定行为。此前直接以返回值判成功，
    /// 会让「权限没拿到」伪装成「权限拿到了」，随后 ExitWindowsEx 以 ACCESS_DENIED 失败，
    /// 日志里呈现的失败原因指向错误的环节，排查会被带偏。因此这里显式复核 LastError。
    ///
    /// 错误码在 <c>CloseHandle</c> 之前就地取出：CloseHandle 未声明 SetLastError，
    /// 不会覆盖托管侧缓存的错误码，但就地取值不依赖这一实现细节。
    /// </summary>
    private static bool EnableShutdownPrivilege(out int nativeErrorCode)
    {
        nativeErrorCode = ErrorSuccess;

        if (!OpenProcessToken(
                GetCurrentProcess(),
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                out var token))
        {
            nativeErrorCode = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(
                    systemName: null,
                    "SeShutdownPrivilege",
                    out var luid))
            {
                nativeErrorCode = Marshal.GetLastWin32Error();
                return false;
            }

            var tokenPrivileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            if (!AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    ref tokenPrivileges,
                    bufferLength: 0,
                    previousState: IntPtr.Zero,
                    returnLength: IntPtr.Zero))
            {
                nativeErrorCode = Marshal.GetLastWin32Error();
                return false;
            }

            // 返回 TRUE 之后必须复核 LastError：典型值 ERROR_NOT_ALL_ASSIGNED(1300)
            // 表示请求的权限一个都没有被启用。
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != ErrorSuccess)
            {
                nativeErrorCode = lastError;
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---- P/Invoke（仅允许本文件） ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    // SetSuspendState 的原生签名收发的都是 BOOLEAN（1 字节），而 C# 的 bool 默认按 Win32
    // BOOL（4 字节）编组。参数侧通常无碍，但**返回值**会把寄存器高位的残留字节一并读进来，
    // 使「睡眠/休眠实际成功」被随机判成失败（或反之）——这类偶发错判极难复现与排查。
    // 因此返回值与三个参数全部显式声明为 U1（1 字节），与原生 BOOLEAN 对齐（S-POWER-D2）。
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool bHibernate,
        [MarshalAs(UnmanagedType.U1)] bool fForce,
        [MarshalAs(UnmanagedType.U1)] bool fWakeupEventsDisabled);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // ---- Native structs ----

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }
}

/// <summary>
/// 空闲检测的 Win32 原生互操作（GetLastInputInfo）。为满足「DllImport 只允许存在于
/// Win32PowerNativeApi.cs」的冻结安全契约，空闲检测的 P/Invoke 与电源调用收敛于同一文件；
/// 业务侧通过 <c>Win32IdleInputSource</c> 消费，绝不直接接触 P/Invoke。
/// </summary>
internal sealed class Win32IdleNativeApi
{
    /// <summary>返回系统上次输入时刻（GetTickCount 时间基准，32 位 tick）；API 失败返回 null。</summary>
    public uint? GetLastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}

/// <summary>
/// 一次性 RTC 唤醒的 Win32 原生互操作（S21）。使用 waitable timer
/// （CreateWaitableTimerExW + SetWaitableTimer fResume=TRUE）实现「睡眠/休眠后自动唤醒」。
/// 为满足「DllImport 只允许存在于 Win32PowerNativeApi.cs」的冻结安全契约，RTC 唤醒的
/// P/Invoke 与电源调用收敛于同一文件；业务侧通过 <c>Win32RtcWakeService</c> 消费，
/// 绝不直接接触 P/Invoke。错误码由各方法在调用点就地捕获（Marshal.GetLastWin32Error）。
/// </summary>
internal sealed class Win32RtcWakeNativeApi : IRtcWakeNativeApi
{
    private const uint TimerAllAccess = 0x001F0003;

    public IntPtr CreateTimer() => CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);

    public NativeCallResult SetWake(IntPtr timer, long dueTimeFileTimeUtc)
    {
        var ok = SetWaitableTimer(timer, ref dueTimeFileTimeUtc, 0, IntPtr.Zero, IntPtr.Zero, fResume: true);
        return ok ? NativeCallResult.Success() : new NativeCallResult(false, Marshal.GetLastWin32Error());
    }

    public NativeCallResult Cancel(IntPtr timer)
    {
        var ok = CancelWaitableTimer(timer);
        return ok ? NativeCallResult.Success() : new NativeCallResult(false, Marshal.GetLastWin32Error());
    }

    public bool Close(IntPtr timer) => CloseHandle(timer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer,
        ref long pDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelWaitableTimer(IntPtr hTimer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
