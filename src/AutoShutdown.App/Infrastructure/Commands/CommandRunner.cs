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
/// 不引入任何 P/Invoke 或 shell 字符串拼接。
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

    private readonly IProcessTreeGateway _processTreeGateway;

    public CommandRunner()
        : this(new DiagnosticProcessTreeGateway())
    {
    }

    public CommandRunner(IProcessTreeGateway processTreeGateway)
    {
        ArgumentNullException.ThrowIfNull(processTreeGateway);
        _processTreeGateway = processTreeGateway;
    }

    public async Task<CommandRunResult> RunCommandAsync(
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
            // 超时（非外部取消）：终止整棵进程树并有界确认根进程与全部已识别后代退出。
            // 仅当根 + 全部已识别后代均确认退出才返回 TimedOut；任何未确认 → CleanupNotConfirmed。
            var (confirmed, _, reason) = KillTreeAndConfirmExit(process);
            return new CommandRunResult
            {
                Status = confirmed ? CommandRunStatus.TimedOut : CommandRunStatus.CleanupNotConfirmed,
                Output = output.ToString(),
                ErrorOutput = errorOutput.ToString(),
                OutputTruncated = output.Truncated || errorOutput.Truncated,
                Duration = stopwatch.Elapsed,
                CleanupFailureReason = confirmed ? null : reason,
                Message = confirmed
                    ? "timed out; process tree terminated."
                    : "timed out; process tree exit not confirmed."
            };
        }
        catch (OperationCanceledException)
        {
            // 外部取消：清理失败不得被静默丢弃，也不得把取消替换为普通异常。
            // 清理已确认 → 原始 OCE 原样传播；清理未确认/清理抛异常 → 可识别 OCE 子类传播
            // （仍满足 OperationCanceledException，携带「进程树清理未确认」语义，绝不携带参数/secret/token/输出）。
            var (confirmed, failure, _) = KillTreeAndConfirmExit(process);
            if (confirmed)
            {
                throw;
            }

            throw new CommandCleanupFailedException(
                "Cancellation occurred; the command process tree exit could not be confirmed.",
                failure ?? new InvalidOperationException("Process tree cleanup did not confirm exit."),
                cancellationToken);
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

    /// <summary>
    /// 受控进程树快照 → 整树终止 → 有界确认全部已识别身份退出（S19-D1 方案A）。
    /// 返回 (Confirmed, Failure, Reason)：Confirmed 仅当「根 + 全部已识别后代」都确认退出时为 true；
    /// 枚举失败、终止失败、任一身份存活/未知均 fail-closed（Confirmed=false，附带脱敏结构化原因）。
    /// 绝不通过根进程的 WaitForExit/HasExited 单独判定整树成功。
    /// </summary>
    private (bool Confirmed, Exception? Failure, CommandCleanupFailureReason Reason) KillTreeAndConfirmExit(
        Process process)
    {
        IReadOnlyList<ProcessIdentity> identities;
        try
        {
            identities = _processTreeGateway.SnapshotTree(process.Id);
        }
        catch (Exception exception)
        {
            // 根/后代枚举失败：无法确认整树，fail-closed。
            return (false, exception, CommandCleanupFailureReason.SnapshotFailed);
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 根进程已无关联（已退出）：继续确认后代（可能仍存活）。
        }
        catch (Exception exception)
        {
            // 终止失败（Win32Exception/AggregateException/NotSupportedException 等）：fail-closed。
            return (false, exception, CommandCleanupFailureReason.KillFailed);
        }

        return ConfirmAllExited(identities, process.Id);
    }

    private (bool Confirmed, Exception? Failure, CommandCleanupFailureReason Reason) ConfirmAllExited(
        IReadOnlyList<ProcessIdentity> identities,
        int rootProcessId)
    {
        if (identities.Count == 0)
        {
            // 未识别到任何进程（含根）：异常状态，fail-closed。
            return (false, new InvalidOperationException("No process tree members were identified."),
                CommandCleanupFailureReason.SnapshotFailed);
        }

        var deadline = Environment.TickCount64 + KillConfirmTimeoutMs;
        Exception? failure = null;
        CommandCleanupFailureReason lastReason = CommandCleanupFailureReason.ConfirmationTimedOut;

        while (true)
        {
            var allExited = true;
            foreach (var identity in identities)
            {
                ProcessLiveness liveness;
                try
                {
                    liveness = _processTreeGateway.CheckLiveness(identity);
                }
                catch (Exception exception)
                {
                    // 存活查询异常（访问拒绝等）：记作 Unknown，fail-closed。
                    liveness = ProcessLiveness.Unknown;
                    failure ??= exception;
                }

                if (liveness != ProcessLiveness.Exited)
                {
                    allExited = false;
                    lastReason = liveness switch
                    {
                        ProcessLiveness.Alive => identity.ProcessId == rootProcessId
                            ? CommandCleanupFailureReason.RootAlive
                            : CommandCleanupFailureReason.ChildAlive,
                        _ => CommandCleanupFailureReason.LivenessUnknown
                    };
                    break;
                }
            }

            if (allExited)
            {
                return (true, null, CommandCleanupFailureReason.Unknown);
            }

            if (Environment.TickCount64 >= deadline)
            {
                return (false, failure
                    ?? new InvalidOperationException("Process tree exit was not confirmed within the deadline."),
                    lastReason);
            }

            Thread.Sleep(50);
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
