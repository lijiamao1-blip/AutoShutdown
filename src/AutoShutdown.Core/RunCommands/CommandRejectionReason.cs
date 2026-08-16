namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 白名单拒绝原因（S19）。每个拒绝命令都携带明确的结构化原因，供审计与测试断言；
/// 默认拒绝（fail-closed）：未知/解析失败/路径不存在/未匹配白名单一律不执行。
/// </summary>
public enum CommandRejectionReason
{
    Unknown = 0,

    /// <summary>可执行路径为空。</summary>
    ExecutableEmpty = 1,

    /// <summary>可执行路径非绝对路径。</summary>
    ExecutableNotAbsolute = 2,

    /// <summary>可执行路径含路径穿越（解析后逃逸允许根）。</summary>
    ExecutableTraversal = 3,

    /// <summary>未匹配任何白名单条目（默认拒绝）。</summary>
    NotWhitelisted = 4,

    /// <summary>参数与白名单参数模式不匹配。</summary>
    ArgumentMismatch = 5
}
