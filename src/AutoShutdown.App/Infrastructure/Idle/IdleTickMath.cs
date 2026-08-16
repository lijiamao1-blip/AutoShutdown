namespace AutoShutdown.App.Infrastructure.Idle;

/// <summary>
/// 32 位系统 tick 的无符号空闲时长计算，正确处理 tick 回绕（约 49.7 天）。
/// </summary>
public static class IdleTickMath
{
    /// <summary>返回 currentTick - lastInputTick（无符号模 2^32），即空闲毫秒数。</summary>
    public static uint ComputeIdleMilliseconds(uint currentTick, uint lastInputTick)
        => unchecked(currentTick - lastInputTick);
}
