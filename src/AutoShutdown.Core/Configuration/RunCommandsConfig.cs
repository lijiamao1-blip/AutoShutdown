using AutoShutdown.Core.RunCommands;

namespace AutoShutdown.Core.Configuration;

/// <summary>
/// RunCommands 配置段（S19）。本地白名单默认空（Commands 空 = 不执行任何命令）；
/// 与 S23 远程命令白名单完全隔离。损坏/非法命令由 <see cref="ConfigurationValidator"/> 与
/// <see cref="CommandWhitelist"/> 双重校验，绝不静默回退为可能执行命令的默认值。
/// </summary>
public sealed record RunCommandsConfig
{
    /// <summary>每命令默认超时（秒）。默认 30。</summary>
    public int DefaultTimeoutSeconds { get; init; } = 30;

    /// <summary>本地命令白名单（默认空 = 默认拒绝）。</summary>
    public LocalCommandWhitelist Whitelist { get; init; } = new();

    /// <summary>按序执行的命令列表（默认空）。</summary>
    public CommandConfig[] Commands { get; init; } = [];
}

/// <summary>
/// 单个待执行命令（S19）。Executable 为具体可执行文件绝对路径（不含通配符）；
/// Arguments 为逐参数字面量；WorkingDirectory 可选（必须为绝对路径）；FailurePolicy
/// 为逐命令 block/continue（默认 block，fail-closed）。
/// </summary>
public sealed record CommandConfig
{
    /// <summary>可执行文件绝对路径。</summary>
    public string Executable { get; init; } = string.Empty;

    /// <summary>参数（逐参数字面量，不拼接、不经过 shell）。</summary>
    public string[] Arguments { get; init; } = [];

    /// <summary>工作目录（可选，绝对路径）。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>逐命令超时覆盖（秒）；未设置用全局默认。</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>逐命令失败策略："block" 或 "continue"（大小写不敏感）；缺省 = block。</summary>
    public string? FailurePolicy { get; init; }
}
