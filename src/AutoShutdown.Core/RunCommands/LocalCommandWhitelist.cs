namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 本地命令白名单（S19）。默认空（Allow 为空 = 不授权任何命令，默认拒绝）。
/// 只由本地配置读取；远程层（S23）没有任何可写入或绕过本白名单的接口。
/// 与 S23 远程命令白名单完全隔离，绝不混用。
/// </summary>
public sealed record LocalCommandWhitelist
{
    /// <summary>允许的命令条目。空 = 默认拒绝一切命令。</summary>
    public WhitelistEntry[] Allow { get; init; } = [];
}

/// <summary>
/// 单个白名单条目（S19）。Executable 为规范化绝对路径（可含受限通配符：目录前缀 "*" 或
/// 受限扩展 "*.ext"）；Arguments 为逐位置字面量参数模式（"*" 匹配任意单个参数），
/// 空 = 仅允许无参数形式。
/// </summary>
public sealed record WhitelistEntry
{
    /// <summary>允许的可执行文件模式（规范化绝对路径 + 受限通配符）。</summary>
    public string Executable { get; init; } = string.Empty;

    /// <summary>允许的参数（逐位置字面量；"*" 匹配任意单个参数）。空 = 仅允许无参数。</summary>
    public string[] Arguments { get; init; } = [];
}
