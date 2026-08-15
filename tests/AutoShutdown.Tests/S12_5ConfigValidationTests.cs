using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.5 配置校验测试。验证 RealPowerEnabled 与 TestMode 互斥规则。
/// </summary>
public sealed class S12_5ConfigValidationTests
{
    // ---- 1. RealPowerEnabled=true 且 TestMode=true → 校验失败 ----

    [Fact]
    public void RealPowerEnabled_WithTestModeTrue_IsInvalid()
    {
        var config = new AppConfig
        {
            SchemaVersion = 1,
            TestMode = true,
            RealPowerEnabled = true,
            AllowedActions = [PowerAction.Shutdown],
            Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
        };

        var errors = ConfigurationValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("RealPowerEnabled"));
    }

    // ---- 2. RealPowerEnabled=false 且 TestMode=true → 合法 ----

    [Fact]
    public void TestModeTrue_RealPowerDisabled_IsValid()
    {
        var config = new AppConfig
        {
            SchemaVersion = 1,
            TestMode = true,
            RealPowerEnabled = false,
            AllowedActions = [PowerAction.Shutdown],
            Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
        };

        var errors = ConfigurationValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("RealPowerEnabled"));
    }

    // ---- 3. RealPowerEnabled=true 且 TestMode=false → 合法 ----

    [Fact]
    public void RealPowerEnabled_WithTestModeFalse_IsValid()
    {
        var config = new AppConfig
        {
            SchemaVersion = 1,
            TestMode = false,
            RealPowerEnabled = true,
            AllowedActions = [PowerAction.Shutdown],
            Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
        };

        var errors = ConfigurationValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("RealPowerEnabled"));
    }

    // ---- 4. 默认配置（TestMode=true、RealPowerEnabled=false）合法 ----

    [Fact]
    public void DefaultConfig_IsValid()
    {
        var errors = ConfigurationValidator.Validate(new AppConfig
        {
            AllowedActions = [PowerAction.Shutdown],
            Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
        });

        Assert.Empty(errors);
    }
}
