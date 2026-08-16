namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 整树清理失败的结构化脱敏原因（S19-D1 诊断 / D2A）。仅用于测试断言与脱敏诊断：
/// 只含类别，绝不携带命令参数、输出、secret、token 或用户路径。
/// </summary>
public enum CommandCleanupFailureReason
{
    Unknown = 0,

    /// <summary>进程树快照失败（枚举/身份读取失败）。</summary>
    SnapshotFailed,

    /// <summary>整树终止失败（Kill 抛异常）。</summary>
    KillFailed,

    /// <summary>确认期限内根进程仍存活。</summary>
    RootAlive,

    /// <summary>确认期限内某已识别后代仍存活。</summary>
    ChildAlive,

    /// <summary>存活复核返回 Unknown（身份/状态无法确认，fail-closed）。</summary>
    LivenessUnknown,

    /// <summary>有界确认期限届满且无更具体原因。</summary>
    ConfirmationTimedOut
}
