namespace AutoShutdown.Core.RunCommands;

/// <summary>单命令执行结局（S19）。区分成功、非零退出、超时、启动失败与「清理未确认」，
/// 形成结构化可审计结果；清理未确认绝不伪装成成功。</summary>
public enum CommandRunStatus
{
    Unknown = 0,

    /// <summary>退出码 0。</summary>
    Success = 1,

    /// <summary>正常退出但退出码非 0。</summary>
    NonZeroExit = 2,

    /// <summary>超过每命令期限，进程树已终止且确认退出。</summary>
    TimedOut = 3,

    /// <summary>启动失败（文件不存在/无权限/非法工作目录等），未执行。</summary>
    LaunchFailed = 4,

    /// <summary>超时后进程树已终止但退出未确认（fail-closed，绝不报成功）。</summary>
    CleanupNotConfirmed = 5
}
