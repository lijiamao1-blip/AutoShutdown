using AutoShutdown.Core.RunCommands;

namespace AutoShutdown.Core.Abstractions;

/// <summary>
/// 命令进程启动边界（S19）。用 ProcessStartInfo.ArgumentList 等无 shell 注入方式启动进程；
/// 捕获退出码、每命令独立超时、取消与限量输出。超时/取消必须终止整棵进程树并有界确认退出，
/// 清理失败不得伪装成功。绝不通过 cmd/powershell/shell 字符串拼接执行不受控输入。
/// 自动化测试用替身实现，绝不启动真实进程。
/// </summary>
public interface ICommandRunner
{
    Task<CommandRunResult> RunAsync(CommandSpec command, CancellationToken cancellationToken);
}
