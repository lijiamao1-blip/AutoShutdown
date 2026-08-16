using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.PrePipeline;

namespace AutoShutdown.Core.RunCommands;

/// <summary>
/// RunCommands 关机前命令编排（S19）。按配置顺序逐条执行：配置加载失败即 fail-closed（不执行
/// 任何命令）；每条命令先经 <see cref="CommandWhitelist"/> 授权（默认拒绝），再用规范化绝对路径
/// 构造 <see cref="CommandSpec"/> 交给 <see cref="ICommandRunner"/>（唯一进程启动边界）串行执行；
/// 逐命令 block/continue（默认 block）。日志/摘要脱敏：绝不记录参数、完整输出或凭据。
/// 不触碰电源服务边界（唯一电源出口不变）、不改变任务终态。
/// </summary>
public sealed class RunCommandsService
{
    private const int MaxSummaryLength = 200;

    private readonly IConfigurationService _configurationService;
    private readonly ICommandRunner _commandRunner;

    public RunCommandsService(
        IConfigurationService configurationService,
        ICommandRunner commandRunner)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(commandRunner);
        _configurationService = configurationService;
        _commandRunner = commandRunner;
    }

    public async Task<RunCommandsReport> RunAllAsync(CancellationToken cancellationToken)
    {
        AppConfig config;
        try
        {
            var load = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (load.Status != ConfigurationLoadStatus.Success || load.Config is null)
            {
                return Failed("The configuration is unavailable; no commands were executed.");
            }

            config = load.Config;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failed("The configuration could not be loaded; no commands were executed.");
        }

        // 清单校验（fail-closed）：损坏的 RunCommands 段绝不静默回退为可能执行命令的默认值。
        if (config.RunCommands is null)
        {
            return Failed("The RunCommands section is invalid; no commands were executed.");
        }

        var section = config.RunCommands;
        var commands = section.Commands ?? [];
        if (commands.Length == 0)
        {
            return new RunCommandsReport { CommandCount = 0, Succeeded = true };
        }

        var defaultTimeout = TimeSpan.FromSeconds(section.DefaultTimeoutSeconds);
        var whitelist = section.Whitelist ?? new LocalCommandWhitelist();

        var results = new List<RunCommandResultEntry>(commands.Length);
        var executedCount = 0;
        var succeededCount = 0;
        var blocked = false;

        for (var index = 0; index < commands.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var command = commands[index];
            if (command is null)
            {
                // 配置校验已保证非 null；防御性收敛为阻断级失败（fail-closed）。
                blocked = true;
                break;
            }

            var policy = ParsePolicy(command.FailurePolicy);

            var decision = CommandWhitelist.Authorize(whitelist, command.Executable, command.Arguments);
            if (!decision.Allowed)
            {
                results.Add(new RunCommandResultEntry
                {
                    Index = index,
                    ExecutablePath = command.Executable,
                    Executed = false,
                    RejectionReason = decision.RejectionReason,
                    Succeeded = false
                });

                if (policy != FailurePolicy.Continue)
                {
                    blocked = true;
                    break;
                }

                continue;
            }

            var timeoutSeconds = command.TimeoutSeconds ?? section.DefaultTimeoutSeconds;
            var spec = new CommandSpec
            {
                ExecutablePath = decision.NormalizedExecutablePath,
                Arguments = command.Arguments ?? [],
                WorkingDirectory = command.WorkingDirectory,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

            var run = await _commandRunner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            executedCount++;

            var succeeded = run.Status == CommandRunStatus.Success;
            if (succeeded)
            {
                succeededCount++;
            }

            results.Add(new RunCommandResultEntry
            {
                Index = index,
                ExecutablePath = decision.NormalizedExecutablePath,
                Executed = true,
                Status = run.Status,
                ExitCode = run.ExitCode,
                Duration = run.Duration,
                Succeeded = succeeded
            });

            if (!succeeded && policy != FailurePolicy.Continue)
            {
                blocked = true;
                break;
            }
        }

        return new RunCommandsReport
        {
            CommandCount = commands.Length,
            ExecutedCount = executedCount,
            SucceededCount = succeededCount,
            Results = results,
            Succeeded = !blocked,
            Summary = blocked || results.Any(result => !result.Succeeded)
                ? BuildSummary(results)
                : string.Empty
        };
    }

    private static FailurePolicy ParsePolicy(string? value) =>
        string.Equals(value, "continue", StringComparison.OrdinalIgnoreCase)
            ? FailurePolicy.Continue
            : FailurePolicy.Block;

    private static string BuildSummary(IReadOnlyList<RunCommandResultEntry> results)
    {
        var failed = results.Where(result => !result.Succeeded).Select(Describe);
        var summary = string.Join("; ", failed);
        return summary.Length <= MaxSummaryLength ? summary : summary[..MaxSummaryLength];
    }

    private static string Describe(RunCommandResultEntry result)
    {
        if (!result.Executed)
        {
            return $"{result.Index}: rejected ({ReasonLabel(result.RejectionReason)})";
        }

        return $"{result.Index}: {StatusLabel(result.Status)}";
    }

    private static string StatusLabel(CommandRunStatus status) => status switch
    {
        CommandRunStatus.Success => "succeeded",
        CommandRunStatus.NonZeroExit => "non-zero exit",
        CommandRunStatus.TimedOut => "timed out",
        CommandRunStatus.LaunchFailed => "launch failed",
        CommandRunStatus.CleanupNotConfirmed => "cleanup not confirmed",
        _ => "unknown"
    };

    private static string ReasonLabel(CommandRejectionReason reason) => reason switch
    {
        CommandRejectionReason.ExecutableEmpty => "empty executable",
        CommandRejectionReason.ExecutableNotAbsolute => "not absolute",
        CommandRejectionReason.ExecutableTraversal => "path traversal",
        CommandRejectionReason.NotWhitelisted => "not whitelisted",
        CommandRejectionReason.ArgumentMismatch => "argument mismatch",
        _ => "unknown"
    };

    private static RunCommandsReport Failed(string summary) => new()
    {
        CommandCount = 0,
        Succeeded = false,
        Summary = summary
    };
}
