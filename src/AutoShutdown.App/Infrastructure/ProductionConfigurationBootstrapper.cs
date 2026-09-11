using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;

namespace AutoShutdown.App.Infrastructure;

/// <summary>正式分发构建在调度器启动前建立真实电源配置；测试构建不调用此入口。</summary>
public static class ProductionConfigurationBootstrapper
{
#if AUTOSHUTDOWN_PRODUCTION
    public const bool IsProductionBuild = true;
#else
    public const bool IsProductionBuild = false;
#endif

    public static async Task<ProductionBootstrapResult> EnsureRealPowerModeAsync(
        IConfigurationService configurationService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configurationService);

        var load = await configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        AppConfig desired;
        string change;
        if (load.Status == ConfigurationLoadStatus.Missing)
        {
            desired = NewProductionConfig();
            change = "已创建正式分发配置";
        }
        else if (load.Status == ConfigurationLoadStatus.Success && load.Config is { } existing)
        {
            if (!existing.TestMode && existing.RealPowerEnabled)
            {
                return new ProductionBootstrapResult(true, false, "已处于真实电源模式");
            }

            desired = existing with { TestMode = false, RealPowerEnabled = true };
            change = "已将现有测试配置切换为真实电源模式";
        }
        else
        {
            return new ProductionBootstrapResult(
                false,
                false,
                "现有配置不可读取，未覆盖配置：" + load.Status);
        }

        var save = await configurationService.SaveAsync(desired, cancellationToken).ConfigureAwait(false);
        return save.Status == ConfigurationSaveStatus.Success
            ? new ProductionBootstrapResult(true, true, change)
            : new ProductionBootstrapResult(false, false,
                "正式分发配置写入失败：" + string.Join("；", save.Errors));
    }

    private static AppConfig NewProductionConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        StartWithWindows = false,
        AllowedActions =
        [
            PowerAction.Shutdown,
            PowerAction.Restart,
            PowerAction.Sleep,
            PowerAction.Hibernate
        ],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };
}

public sealed record ProductionBootstrapResult(bool Succeeded, bool Changed, string Message);
