using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Configuration;

public static class ConfigurationValidator
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxSecondsPerDay = 86400;
    private const int MaxRetentionDays = 365;

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
