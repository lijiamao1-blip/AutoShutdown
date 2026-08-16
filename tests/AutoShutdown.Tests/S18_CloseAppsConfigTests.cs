using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18 C2 配置/清单校验测试。验证 CloseApps 段的结构校验与规范化：稳定标识（路径/pid）
/// 恰取其一、超时边界、重复目标、强杀默认关闭、损坏配置不静默回退。
/// </summary>
public sealed class S18_CloseAppsConfigTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    [Fact]
    public void DefaultConfig_EmptyTargets_IsValid()
    {
        var errors = ConfigurationValidator.Validate(ValidConfig());

        Assert.Empty(errors);
    }

    [Fact]
    public void TargetWithPathOnly_IsValid()
    {
        var config = ValidConfig(new CloseAppsTargetConfig { ExecutablePath = NotepadPath });

        Assert.Empty(ConfigurationValidator.Validate(config));
    }

    [Fact]
    public void TargetWithPidOnly_IsValid()
    {
        var config = ValidConfig(new CloseAppsTargetConfig { ProcessId = 4242 });

        Assert.Empty(ConfigurationValidator.Validate(config));
    }

    [Fact]
    public void TargetWithBothPathAndPid_IsInvalid()
    {
        var config = ValidConfig(new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ProcessId = 4242 });

        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("exactly one"));
    }

    [Fact]
    public void TargetWithNeither_IsInvalid()
    {
        var config = ValidConfig(new CloseAppsTargetConfig());

        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("exactly one"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TargetWithNonPositivePid_IsInvalid(int pid)
    {
        var config = ValidConfig(new CloseAppsTargetConfig { ProcessId = pid });

        Assert.NotEmpty(ConfigurationValidator.Validate(config));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void GlobalGracefulTimeout_OutOfBounds_IsInvalid(int seconds)
    {
        var config = ValidConfig();
        config = config with { CloseApps = config.CloseApps with { GracefulTimeoutSeconds = seconds } };

        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("GracefulTimeoutSeconds"));
    }

    [Fact]
    public void PerTargetTimeout_OutOfBounds_IsInvalid()
    {
        var config = ValidConfig(new CloseAppsTargetConfig
        {
            ExecutablePath = NotepadPath,
            GracefulTimeoutSeconds = 301
        });

        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("GracefulTimeoutSeconds"));
    }

    [Fact]
    public void DuplicatePath_IsInvalid()
    {
        var config = ValidConfig(
            new CloseAppsTargetConfig { ExecutablePath = NotepadPath },
            new CloseAppsTargetConfig { ExecutablePath = NotepadPath.ToUpperInvariant() });

        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("duplicated"));
    }

    [Fact]
    public void DuplicatePid_IsInvalid()
    {
        var config = ValidConfig(
            new CloseAppsTargetConfig { ProcessId = 42 },
            new CloseAppsTargetConfig { ProcessId = 42 });

        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("duplicated"));
    }

    [Fact]
    public void NullTargets_IsInvalid()
    {
        var config = ValidConfig();
        config = config with { CloseApps = config.CloseApps with { Targets = null! } };

        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("Targets"));
    }

    [Fact]
    public void ForceKillAllowed_DefaultsToFalse()
    {
        // 未授权强杀默认关闭：配置未显式开启时，规范化目标 ForceKillAllowed=false。
        var config = new CloseAppsConfig
        {
            Targets = [new CloseAppsTargetConfig { ExecutablePath = NotepadPath }]
        };

        var targets = CloseAppsTargetList.Normalize(config, TimeSpan.FromSeconds(30));

        Assert.Single(targets);
        Assert.False(targets[0].ForceKillAllowed);
    }

    [Fact]
    public void Normalize_ComputesSanitizedTargetId_AndConvertsTimeout()
    {
        var config = new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 30,
            Targets =
            [
                new CloseAppsTargetConfig { ExecutablePath = NotepadPath, GracefulTimeoutSeconds = 10 },
                new CloseAppsTargetConfig { ProcessId = 77 }
            ]
        };

        var targets = CloseAppsTargetList.Normalize(config, TimeSpan.FromSeconds(30));

        Assert.Equal(2, targets.Count);
        Assert.Equal("notepad.exe", targets[0].TargetId); // 仅文件名，不含全路径
        Assert.Equal(TimeSpan.FromSeconds(10), targets[0].GracefulTimeout);
        Assert.Equal("pid:77", targets[1].TargetId);
        Assert.Equal(TimeSpan.FromSeconds(30), targets[1].GracefulTimeout);
    }

    private static AppConfig ValidConfig(params CloseAppsTargetConfig[] targets) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        CloseApps = new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 30,
            Targets = targets
        }
    };
}
