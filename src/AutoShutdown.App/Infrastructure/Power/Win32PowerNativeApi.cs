using System.Runtime.InteropServices;

namespace AutoShutdown.App.Infrastructure.Power;

/// <summary>
/// 真实 Windows 电源 API 的唯一实现。本文件是全代码库唯一允许包含电源 P/Invoke 的位置：
/// <see cref="ExitWindowsEx"/>（关机/重启）、<see cref="SetSuspendState"/>（睡眠/休眠），
/// 以及获取 SE_SHUTDOWN_NAME 权限所需的 token 操作。
/// 禁止在本文件之外出现任何真实电源调用；禁止通过系统命令行、脚本或进程启动方式执行电源操作。
/// </summary>
public sealed class Win32PowerNativeApi : IPowerNativeApi
{
    // ExitWindowsEx flags
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_REBOOT = 0x00000002;
    private const uint EWX_POWEROFF = 0x00000008;

    // SetSuspendState 参数
    private const bool SleepMode = false;    // 睡眠
    private const bool HibernateMode = true; // 休眠

    // Token privileges
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    public (bool Succeeded, int? NativeErrorCode) Shutdown()
    {
        if (!EnableShutdownPrivilege())
        {
            return (false, Marshal.GetLastWin32Error());
        }

        var ok = ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, 0);
        return ok ? (true, null) : (false, Marshal.GetLastWin32Error());
    }

    public (bool Succeeded, int? NativeErrorCode) Restart()
    {
        if (!EnableShutdownPrivilege())
        {
            return (false, Marshal.GetLastWin32Error());
        }

        var ok = ExitWindowsEx(EWX_REBOOT, 0);
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

    /// <summary>为关机/重启获取 SE_SHUTDOWN_NAME 权限；失败时返回 false 并设置 LastWin32Error。</summary>
    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(
                    systemName: null,
                    "SeShutdownPrivilege",
                    out var luid))
            {
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

            return AdjustTokenPrivileges(
                token,
                disableAllPrivileges: false,
                ref tokenPrivileges,
                bufferLength: 0,
                previousState: IntPtr.Zero,
                returnLength: IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---- P/Invoke（仅允许本文件） ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool bHibernate, bool fForce, bool fWakeupEventsDisabled);

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
