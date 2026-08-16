namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 进程稳定身份（S19-D1）。以进程 ID + 启动时间（UTC）识别一个进程，避免 PID 被系统复用后
/// 误判新进程为原进程。启动时间无法读取时为 default，<see cref="StartTimeKnown"/> 为 false，
/// 后续存活复核将 fail-closed（Unknown）。
/// </summary>
public sealed record ProcessIdentity
{
    public int ProcessId { get; init; }

    /// <summary>进程启动时间（UTC）；无法读取时为 default（未知）。</summary>
    public DateTimeOffset StartTimeUtc { get; init; }

    /// <summary>启动时间是否已成功读取（default 视为未知）。</summary>
    public bool StartTimeKnown => StartTimeUtc != default;
}
