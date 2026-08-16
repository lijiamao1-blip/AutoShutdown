using System.Diagnostics;
using System.Text;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.RunCommands;

namespace AutoShutdown.App.Infrastructure.Commands;

/// <summary>
/// 基于 System.Diagnostics.Process 的真实命令执行器（S19）。唯一进程启动边界；
/// 用 ProcessStartInfo.ArgumentList（逐字面量，无 shell 拼接），工作目录与环境变量显式最小化。
/// 捕获退出码、每命令独立超时、取消与限量输出；超时/取消时终止整棵进程树
/// （Process.Kill(entireProcessTree: true)）并有界确认退出，清理未确认绝不报成功。
/// 不引入任何 P/Invoke、cmd/PowerShell/shell 字符串拼接。
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    private const int MaxOutputCharsPerStream = 8192;
    private const int KillConfirmTimeoutMs = 3000;

    private static readonly string[] MinimalEnvironmentKeys =
    [
        "SystemRoot", "SystemDrive", "WINDIR", "TEMP", "TMP",
        "PATH", "PATHEXT", "COMSPEC", "OS", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE"
    ];

    public async Task<CommandRunResult> RunAsync(
        CommandSpec command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Timeout must be positive.");
        }

        var stopwatch = Stopwatch.StartNew();

        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (command.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = command.WorkingDirectory;
        }

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        MinimizeEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };

        var output = new BoundedTextBuffer(MaxOutputCharsPerStream);
        var errorOutput = new BoundedTextBuffer(MaxOutputCharsPerStream);
        process.OutputDataReceived += (_, eventArgs) => output.AppendLine(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => errorOutput.AppendLine(eventArgs.Data);

        try
        {
            if (!process.Start())
            {
                return Failed(CommandRunStatus.LaunchFailed, stopwatch.Elapsed, output, errorOutput);
            }
        }
        catch
        {
            // 文件不存在 / 无权限 / 非法工作目录等：未执行，fail-closed。
            return Failed(CommandRunStatus.LaunchFailed, stopwatch.Elapsed, output, errorOutput);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(command.Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            process.WaitForExit(); // 确保异步输出事件全部送达。

            var exitCode = process.ExitCode;
            return new CommandRunResult
            {
                Status = exitCode == 0 ? CommandRunStatus.Success : CommandRunStatus.NonZeroExit,
                ExitCode = exitCode,
                Output = output.ToString(),
                ErrorOutput = errorOutput.ToString(),
                OutputTruncated = output.Truncated || errorOutput.Truncated,
                Duration = stopwatch.Elapsed,
                Message = exitCode == 0 ? string.Empty : $"exited with code {exitCode}."
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时（非外部取消）：终止整棵进程树并有界确认退出。
            var confirmed = KillTreeAndConfirmExit(process);
            return new CommandRunResult
            {
                Status = confirmed ? CommandRunStatus.TimedOut : CommandRunStatus.CleanupNotConfirmed,
                Output = output.ToString(),
                ErrorOutput = errorOutput.ToString(),
                OutputTruncated = output.Truncated || errorOutput.Truncated,
                Duration = stopwatch.Elapsed,
                Message = confirmed
                    ? "timed out; process tree terminated."
                    : "timed out; process tree exit not confirmed."
            };
        }
        catch (OperationCanceledException)
        {
            // 外部取消：终止进程树（尽力而为），取消以 OCE 传播，绝不伪装成功。
            KillTreeAndConfirmExit(process);
            throw;
        }
    }

    private static CommandRunResult Failed(
        CommandRunStatus status,
        TimeSpan duration,
        BoundedTextBuffer output,
        BoundedTextBuffer errorOutput) => new()
    {
        Status = status,
        Output = output.ToString(),
        ErrorOutput = errorOutput.ToString(),
        OutputTruncated = output.Truncated || errorOutput.Truncated,
        Duration = duration,
        Message = "The process failed to start."
    };

    private static bool KillTreeAndConfirmExit(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 无关联进程：已退出。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 访问拒绝等：落到退出确认。
        }

        try
        {
            if (process.WaitForExit(KillConfirmTimeoutMs))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return true; // 无关联进程 = 已退出。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 落到 HasExited 复核。
        }

        try
        {
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void MinimizeEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.EnvironmentVariables.Clear();
        foreach (var key in MinimalEnvironmentKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }
    }

    private sealed class BoundedTextBuffer
    {
        private readonly int _maxChars;
        private readonly StringBuilder _builder = new();
        private int _chars;

        public BoundedTextBuffer(int maxChars) => _maxChars = maxChars;

        public bool Truncated { get; private set; }

        public void AppendLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var remaining = _maxChars - _chars;
            if (remaining <= 0)
            {
                Truncated = true;
                return;
            }

            var toAppend = text.Length <= remaining ? text : text[..remaining];
            _builder.AppendLine(toAppend);
            _chars += toAppend.Length + Environment.NewLine.Length;
            if (text.Length > remaining)
            {
                Truncated = true;
            }
        }

        public override string ToString() => _builder.ToString();
    }
}
