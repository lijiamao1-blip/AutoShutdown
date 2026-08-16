namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// 规范化后的待执行命令（S19）。ExecutablePath 为白名单授权返回的规范化绝对路径；
/// Arguments 为逐参数字面量列表（绝不拼接成 shell 字符串）。工作目录可选（绝对路径），
/// 环境变量由执行器显式最小化。超时为每命令独立期限。
/// </summary>
public sealed record CommandSpec
{
    public string ExecutablePath { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
