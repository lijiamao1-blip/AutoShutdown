using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.App.Infrastructure.Idle;

/// <summary>
/// Win32 GetLastInputInfo 空闲输入源：返回距上次输入的空闲时长；API 失败返回 null。
/// 使用无符号 tick 减法以正确处理 32 位 tick 回绕（约 49.7 天）。
/// 实际的原生互操作收敛于 <see cref="Win32IdleNativeApi"/>（唯一原生调用网关）。
/// </summary>
public sealed class Win32IdleInputSource : IIdleInputSource
{
    private readonly Win32IdleNativeApi _native = new();

    public TimeSpan? GetIdleDuration()
    {
        var lastInputTick = _native.GetLastInputTick();
        if (lastInputTick is null)
        {
            return null;
        }

        var idleMilliseconds = IdleTickMath.ComputeIdleMilliseconds(
            unchecked((uint)Environment.TickCount),
            lastInputTick.Value);

        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }
}
