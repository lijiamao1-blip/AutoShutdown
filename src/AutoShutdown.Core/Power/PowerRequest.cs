using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Power;

public sealed record PowerRequest
{
    public PowerAction Action { get; init; } = PowerAction.Unknown;

    public Guid InstanceId { get; init; }

    public string Reason { get; init; } = string.Empty;

    /// <summary>是否已获得用户对本次真实电源执行的人工确认（双闸门之二）。默认 false。</summary>
    public bool RealPowerConfirmed { get; init; }

    /// <summary>关机/重启时是否允许 Windows 强制结束无响应应用。默认 false。</summary>
    public bool ForceIfHung { get; init; }
}
