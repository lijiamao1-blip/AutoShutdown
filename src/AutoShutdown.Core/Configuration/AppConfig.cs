using AutoShutdown.Core.State;

namespace AutoShutdown.Core.Configuration;

public sealed record AppConfig
{
    public int SchemaVersion { get; init; } = 1;

    public bool TestMode { get; init; } = true;

    /// <summary>
    /// 发布配置级真实电源开关（双闸门之一）。默认 false。
    /// 与 TestMode=true 互斥；仅当 TestMode=false 且 RealPowerEnabled=true 时才可能走真实电源。
    /// </summary>
    public bool RealPowerEnabled { get; init; }

    public int DefaultWarningSeconds { get; init; } = 60;

    public int DefaultSnoozeSeconds { get; init; } = 300;

    public bool StartWithWindows { get; init; }

    public PowerAction[] AllowedActions { get; init; } = [];

    public bool MinimizeToTrayOnClose { get; init; } = true;

    public LoggingConfig Logging { get; init; } = new();
}

public sealed record LoggingConfig
{
    public LogLevel Level { get; init; } = LogLevel.Information;

    public int RetentionDays { get; init; } = 14;
}
