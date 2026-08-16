using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.RunCommands;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S19 C1 白名单契约测试。验证：路径规范化、前缀边界（碰撞）、大小写不敏感、受限通配符边界、
/// 参数模式匹配、拒绝原因、默认空白名单拒绝，以及 RunCommands 配置段的结构校验。
/// 纯字符串/路径逻辑，不触碰文件系统、不启动进程、不触碰电源。
/// </summary>
public sealed class S19_CommandWhitelistTests
{
    private const string CmdPath = @"C:\Windows\System32\cmd.exe";

    // ---------- 默认拒绝 / 规范化 ----------

    [Fact]
    public void EmptyWhitelist_DeniesEverything()
    {
        var decision = CommandWhitelist.Authorize(new LocalCommandWhitelist(), CmdPath, []);

        Assert.False(decision.Allowed);
        Assert.Equal(CommandRejectionReason.NotWhitelisted, decision.RejectionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyExecutable_IsRejected(string executable)
    {
        var whitelist = Whitelist(Entry(CmdPath));

        var decision = CommandWhitelist.Authorize(whitelist, executable, []);

        Assert.False(decision.Allowed);
        Assert.Equal(CommandRejectionReason.ExecutableEmpty, decision.RejectionReason);
    }

    [Theory]
    [InlineData(@"tools\run.exe")]
    [InlineData(@"C:run.exe")]
    [InlineData(@"\run.exe")]
    public void NonFullyQualifiedExecutable_IsRejected(string executable)
    {
        var whitelist = Whitelist(Entry(CmdPath));

        var decision = CommandWhitelist.Authorize(whitelist, executable, []);

        Assert.False(decision.Allowed);
        Assert.Equal(CommandRejectionReason.ExecutableNotAbsolute, decision.RejectionReason);
    }

    [Fact]
    public void TraversalExecutable_IsRejected()
    {
        var whitelist = Whitelist(Entry(@"C:\Tools\*"));

        var decision = CommandWhitelist.Authorize(
            whitelist, @"C:\Tools\..\Windows\System32\cmd.exe", []);

        Assert.False(decision.Allowed);
        Assert.Equal(CommandRejectionReason.ExecutableTraversal, decision.RejectionReason);
    }

    // ---------- 精确匹配 / 大小写 / 规范化 ----------

    [Fact]
    public void ExactMatch_IsCaseInsensitive_AndReturnsNormalizedPath()
    {
        var whitelist = Whitelist(Entry(CmdPath));

        var decision = CommandWhitelist.Authorize(
            whitelist, @"c:\windows\system32\CMD.EXE", []);

        Assert.True(decision.Allowed);
        Assert.True(Path.IsPathFullyQualified(decision.NormalizedExecutablePath));
        Assert.Equal(CmdPath, decision.NormalizedExecutablePath, ignoreCase: true);
    }

    // ---------- 前缀边界（碰撞） ----------

    [Fact]
    public void PrefixBoundary_DoesNotMatchSiblingDirectory()
    {
        var whitelist = Whitelist(Entry(@"C:\Tools\*"));

        // Tools2 以 Tools 开头，但必须作为独立目录边界处理，绝不匹配。
        var decision = CommandWhitelist.Authorize(whitelist, @"C:\Tools2\evil.exe", []);

        Assert.False(decision.Allowed);
        Assert.Equal(CommandRejectionReason.NotWhitelisted, decision.RejectionReason);
    }

    [Fact]
    public void PrefixBoundary_MatchesWithinAllowedRoot()
    {
        var whitelist = Whitelist(Entry(@"C:\Tools\*"));

        var decision = CommandWhitelist.Authorize(whitelist, @"C:\Tools\run.exe", []);

        Assert.True(decision.Allowed);
    }

    // ---------- 受限通配符边界 ----------

    [Fact]
    public void ExtensionWildcard_MatchesOnlyRestrictedExtension()
    {
        var whitelist = Whitelist(Entry(@"C:\Tools\*.exe"));

        Assert.True(CommandWhitelist.Authorize(whitelist, @"C:\Tools\a.exe", []).Allowed);
        Assert.False(CommandWhitelist.Authorize(whitelist, @"C:\Tools\a.dll", []).Allowed);
        Assert.False(CommandWhitelist.Authorize(whitelist, @"C:\Tools\a.exe.bak", []).Allowed);
    }

    [Fact]
    public void ExtensionWildcard_StillConfirmsUnderRoot()
    {
        // 通配符匹配后必须再次确认仍位于允许根：Tools2 不在 Tools 之下。
        var whitelist = Whitelist(Entry(@"C:\Tools\*.exe"));

        Assert.False(CommandWhitelist.Authorize(whitelist, @"C:\Tools2\a.exe", []).Allowed);
    }

    [Theory]
    [InlineData(@"C:\*\run.exe")]
    [InlineData(@"C:\To*ls\run.exe")]
    [InlineData(@"*.exe")]
    [InlineData(@"C:\Tools\a*b.exe")]
    [InlineData(@"C:\Tools\*.exe.bak")]
    public void InvalidWildcardPatterns_AreRejectedByValidation(string pattern)
    {
        var errors = CommandWhitelist.Validate(Whitelist(Entry(pattern)));

        Assert.Contains(errors, error => error.Contains("Executable"));
    }

    [Fact]
    public void ValidWildcardPatterns_ValidateClean()
    {
        var whitelist = Whitelist(Entry(@"C:\Tools\*"), Entry(@"C:\Tools\*.exe"), Entry(CmdPath));

        Assert.Empty(CommandWhitelist.Validate(whitelist));
    }

    // ---------- 参数模式匹配 ----------

    [Fact]
    public void Arguments_MustMatchPositionally()
    {
        var whitelist = Whitelist(Entry(CmdPath, ["/c", "*"]));

        Assert.True(CommandWhitelist.Authorize(whitelist, CmdPath, ["/c", "echo hello"]).Allowed);
        Assert.False(CommandWhitelist.Authorize(whitelist, CmdPath, ["/d", "echo hello"]).Allowed);
        Assert.False(CommandWhitelist.Authorize(whitelist, CmdPath, ["/c"]).Allowed);
    }

    [Fact]
    public void ArgumentMismatch_HasDistinctReason()
    {
        var whitelist = Whitelist(Entry(CmdPath, ["/c"]));

        var decision = CommandWhitelist.Authorize(whitelist, CmdPath, ["/c", "extra"]);

        Assert.False(decision.Allowed);
        Assert.Equal(CommandRejectionReason.ArgumentMismatch, decision.RejectionReason);
    }

    [Fact]
    public void EmptyArgumentPattern_AllowsOnlyNoArguments()
    {
        var whitelist = Whitelist(Entry(CmdPath));

        Assert.True(CommandWhitelist.Authorize(whitelist, CmdPath, []).Allowed);
        Assert.False(CommandWhitelist.Authorize(whitelist, CmdPath, ["x"]).Allowed);
    }

    [Fact]
    public void StarArgumentWildcard_MatchesAnySingleArgument()
    {
        var whitelist = Whitelist(Entry(CmdPath, ["*"]));

        Assert.True(CommandWhitelist.Authorize(whitelist, CmdPath, ["--token=abc; & | < >"]).Allowed);
        Assert.False(CommandWhitelist.Authorize(whitelist, CmdPath, ["a", "b"]).Allowed);
    }

    [Fact]
    public void MultipleEntries_FirstMatchingArgumentSetWins()
    {
        var whitelist = Whitelist(
            Entry(CmdPath, ["/c"]),
            Entry(CmdPath, ["/c", "*"]));

        Assert.True(CommandWhitelist.Authorize(whitelist, CmdPath, ["/c", "echo hi"]).Allowed);
        Assert.True(CommandWhitelist.Authorize(whitelist, CmdPath, ["/c"]).Allowed);
    }

    // ---------- 配置段结构校验 ----------

    [Fact]
    public void DefaultRunCommandsConfig_IsValid()
    {
        Assert.Empty(ConfigurationValidator.Validate(ValidConfig()));
    }

    [Fact]
    public void NullRunCommands_IsInvalid()
    {
        var config = ValidConfig();
        config = config with { RunCommands = null! };

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains("RunCommands must not be null"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void DefaultTimeout_OutOfBounds_IsInvalid(int seconds)
    {
        var config = ValidConfig();
        config = config with { RunCommands = config.RunCommands with { DefaultTimeoutSeconds = seconds } };

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains("DefaultTimeoutSeconds"));
    }

    [Fact]
    public void CommandWithValidExecutableAndPolicy_IsValid()
    {
        var config = ValidConfig(new CommandConfig
        {
            Executable = CmdPath,
            Arguments = ["/c", "echo hi"],
            FailurePolicy = "continue"
        });

        Assert.Empty(ConfigurationValidator.Validate(config));
    }

    [Theory]
    [InlineData(@"tools\run.exe")]
    [InlineData(@"C:\Tools\*.exe")]
    [InlineData(@"C:\Tools\..\run.exe")]
    public void CommandExecutable_Invalid_IsRejected(string executable)
    {
        var config = ValidConfig(new CommandConfig { Executable = executable });

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains(".Executable"));
    }

    [Fact]
    public void CommandTimeout_OutOfBounds_IsInvalid()
    {
        var config = ValidConfig(new CommandConfig { Executable = CmdPath, TimeoutSeconds = 301 });

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains("TimeoutSeconds"));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("continue")]
    [InlineData("BLOCK")]
    [InlineData("Continue")]
    public void CommandFailurePolicy_ValidValues_AreAccepted(string policy)
    {
        var config = ValidConfig(new CommandConfig { Executable = CmdPath, FailurePolicy = policy });

        Assert.Empty(ConfigurationValidator.Validate(config));
    }

    [Fact]
    public void CommandFailurePolicy_InvalidValue_IsRejected()
    {
        var config = ValidConfig(new CommandConfig { Executable = CmdPath, FailurePolicy = "maybe" });

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains("FailurePolicy"));
    }

    [Fact]
    public void CommandWorkingDirectory_Relative_IsRejected()
    {
        var config = ValidConfig(new CommandConfig
        {
            Executable = CmdPath,
            WorkingDirectory = @"relative\dir"
        });

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains("WorkingDirectory"));
    }

    [Fact]
    public void NullCommands_IsInvalid()
    {
        var config = ValidConfig();
        config = config with { RunCommands = config.RunCommands with { Commands = null! } };

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains("Commands"));
    }

    [Fact]
    public void NullWhitelist_IsInvalid()
    {
        var config = ValidConfig();
        config = config with { RunCommands = config.RunCommands with { Whitelist = null! } };

        Assert.Contains(ConfigurationValidator.Validate(config), error => error.Contains("Whitelist"));
    }

    // ---------- helpers ----------

    private static LocalCommandWhitelist Whitelist(params WhitelistEntry[] entries)
        => new() { Allow = entries };

    private static WhitelistEntry Entry(string executable, params string[] arguments)
        => new() { Executable = executable, Arguments = arguments };

    private static AppConfig ValidConfig(params CommandConfig[] commands) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        RunCommands = new RunCommandsConfig
        {
            DefaultTimeoutSeconds = 30,
            Whitelist = new LocalCommandWhitelist(),
            Commands = commands
        }
    };
}
