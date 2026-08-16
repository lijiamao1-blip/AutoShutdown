using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Configuration;

public static class ConfigurationValidator
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxSecondsPerDay = 86400;
    private const int MaxRetentionDays = 365;
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 300;

    private static readonly HashSet<PowerAction> AllowedActionValues =
        [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate];

    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        if (config.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add(
                $"SchemaVersion must be {CurrentSchemaVersion}, but was {config.SchemaVersion}.");
        }

        if (config.RealPowerEnabled && config.TestMode)
        {
            errors.Add("RealPowerEnabled must not be true while TestMode is true.");
        }

        if (config.DefaultWarningSeconds is < 0 or > MaxSecondsPerDay)
        {
            errors.Add(
                $"DefaultWarningSeconds must be between 0 and {MaxSecondsPerDay}, but was {config.DefaultWarningSeconds}.");
        }

        if (config.DefaultSnoozeSeconds is < 1 or > MaxSecondsPerDay)
        {
            errors.Add(
                $"DefaultSnoozeSeconds must be between 1 and {MaxSecondsPerDay}, but was {config.DefaultSnoozeSeconds}.");
        }

        ValidateAllowedActions(config, errors);
        ValidateLogging(config, errors);
        ValidateCloseApps(config, errors);
        ValidateRunCommands(config, errors);

        return errors;
    }

    private static void ValidateCloseApps(AppConfig config, List<string> errors)
    {
        // 损坏的 CloseApps 段不得静默回退为可能触发关闭/强杀的默认值；null 视为非法。
        if (config.CloseApps is null)
        {
            errors.Add("CloseApps must not be null.");
            return;
        }

        errors.AddRange(CloseAppsTargetList.Validate(config.CloseApps));
    }

    private static void ValidateRunCommands(AppConfig config, List<string> errors)
    {
        // 损坏的 RunCommands 段不得静默回退为可能执行命令的默认值；null 视为非法。
        if (config.RunCommands is null)
        {
            errors.Add("RunCommands must not be null.");
            return;
        }

        if (config.RunCommands.DefaultTimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            errors.Add(
                $"RunCommands.DefaultTimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}.");
        }

        if (config.RunCommands.Whitelist is null)
        {
            errors.Add("RunCommands.Whitelist must not be null.");
        }
        else
        {
            errors.AddRange(CommandWhitelist.Validate(config.RunCommands.Whitelist));
        }

        if (config.RunCommands.Commands is null)
        {
            errors.Add("RunCommands.Commands must not be null.");
            return;
        }

        for (var index = 0; index < config.RunCommands.Commands.Length; index++)
        {
            var command = config.RunCommands.Commands[index];
            var prefix = $"RunCommands.Commands[{index}]";

            if (command is null)
            {
                errors.Add($"{prefix} must not be null.");
                continue;
            }

            var executableError = CommandWhitelist.DescribeExecutablePathError(command.Executable);
            if (executableError is not null)
            {
                errors.Add($"{prefix}.Executable {executableError}");
            }

            if (command.Arguments is null)
            {
                errors.Add($"{prefix}.Arguments must not be null.");
            }
            else if (command.Arguments.Any(argument => argument is null))
            {
                errors.Add($"{prefix}.Arguments must not contain null elements.");
            }

            if (command.WorkingDirectory is not null)
            {
                var workingDirError = CommandWhitelist.DescribeExecutablePathError(command.WorkingDirectory);
                if (workingDirError is not null)
                {
                    errors.Add($"{prefix}.WorkingDirectory {workingDirError}");
                }
            }

            if (command.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            {
                errors.Add(
                    $"{prefix}.TimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}.");
            }

            if (command.FailurePolicy is not null
                && !string.Equals(command.FailurePolicy, "block", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command.FailurePolicy, "continue", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{prefix}.FailurePolicy must be \"block\" or \"continue\".");
            }
        }
    }

    private static void ValidateAllowedActions(AppConfig config, List<string> errors)
    {
        if (config.AllowedActions is null)
        {
            errors.Add("AllowedActions must not be null.");
            return;
        }

        if (config.AllowedActions.Length == 0)
        {
            errors.Add("AllowedActions must contain at least one action.");
            return;
        }

        var seen = new HashSet<PowerAction>();
        foreach (var action in config.AllowedActions)
        {
            if (!AllowedActionValues.Contains(action))
            {
                errors.Add($"AllowedActions contains an invalid action: {action}.");
            }
            else if (!seen.Add(action))
            {
                errors.Add($"AllowedActions contains a duplicate action: {action}.");
            }
        }
    }

    private static void ValidateLogging(AppConfig config, List<string> errors)
    {
        if (config.Logging is null)
        {
            errors.Add("Logging must not be null.");
            return;
        }

        if (config.Logging.Level is not (LogLevel.Information or LogLevel.Warning or LogLevel.Error))
        {
            errors.Add(
                $"Logging.Level must be Information, Warning or Error, but was {config.Logging.Level}.");
        }

        if (config.Logging.RetentionDays is < 1 or > MaxRetentionDays)
        {
            errors.Add(
                $"Logging.RetentionDays must be between 1 and {MaxRetentionDays}, but was {config.Logging.RetentionDays}.");
        }
    }
}
