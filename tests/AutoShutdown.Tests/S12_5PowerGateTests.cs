using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.5 双闸门电源路由测试。
/// 验证 GuardedPowerService 的四个分支：TestMode→Fake；RealPowerEnabled=false→拒绝；
/// 无确认→拒绝；全部通过→Win32。全部使用替身，绝不调用真实电源。
/// </summary>
public sealed class S12_5PowerGateTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- 1. TestMode=true 且 RealPowerEnabled=true 时走 Fake，零 Win32 ----

    [Fact]
    public async Task TestModeTrue_RealPowerEnabledTrue_ForcesFake_ZeroWin32()
    {
        var config = Config(testMode: true, realPowerEnabled: true);
        var (gate, fake, real) = CreateGate(config);

        var result = await gate.ExecuteAsync(Request(confirmed: false), CancellationToken.None);

        Assert.Equal(PowerOutcome.Simulated, result.Outcome);
        Assert.True(result.WasSimulated);
        Assert.Single(fake.Invocations);
        Assert.Equal(0, real.CallCount);
    }

    // ---- 2. TestMode=false 且 RealPowerEnabled=false 时返回 Rejected，零调用 ----

    [Fact]
    public async Task TestModeFalse_RealPowerNotEnabled_Rejected_ZeroPower()
    {
        var config = Config(testMode: false, realPowerEnabled: false);
        var (gate, fake, real) = CreateGate(config);

        var result = await gate.ExecuteAsync(Request(confirmed: true), CancellationToken.None);

        Assert.Equal(PowerOutcome.Rejected, result.Outcome);
        Assert.Empty(fake.Invocations);
        Assert.Equal(0, real.CallCount);
    }

    // ---- 3. TestMode=false、RealPowerEnabled=true、无确认时返回 Rejected ----

    [Fact]
    public async Task RealPowerEnabled_NoConfirmation_Rejected_ZeroWin32()
    {
        var config = Config(testMode: false, realPowerEnabled: true);
        var (gate, fake, real) = CreateGate(config);

        var result = await gate.ExecuteAsync(Request(confirmed: false), CancellationToken.None);

        Assert.Equal(PowerOutcome.Rejected, result.Outcome);
        Assert.Empty(fake.Invocations);
        Assert.Equal(0, real.CallCount);
    }

    // ---- 4. TestMode=false、RealPowerEnabled=true、确认通过时调用 Win32 ----

    [Fact]
    public async Task RealPowerEnabled_WithConfirmation_CallsWin32()
    {
        var config = Config(testMode: false, realPowerEnabled: true);
        var (gate, fake, real) = CreateGate(config);

        var result = await gate.ExecuteAsync(Request(confirmed: true), CancellationToken.None);

        Assert.Equal(PowerOutcome.Accepted, result.Outcome);
        Assert.Empty(fake.Invocations);
        Assert.Equal(1, real.CallCount);
    }

    // ---- 5. 配置加载失败时安全拒绝，不抛异常 ----

    [Fact]
    public async Task ConfigUnavailable_RejectedWithoutThrowing_ZeroPower()
    {
        var gate = new GuardedPowerService(
            new StubConfigurationService(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Corrupt,
                Errors = ["corrupt"]
            }),
            new FakePowerService(),
            new RecordingWin32());

        var result = await gate.ExecuteAsync(Request(confirmed: true), CancellationToken.None);

        Assert.Equal(PowerOutcome.Rejected, result.Outcome);
    }

    // ---- 6. 配置服务抛异常时安全拒绝 ----

    [Fact]
    public async Task ConfigThrows_RejectedWithoutThrowing_ZeroPower()
    {
        var gate = new GuardedPowerService(
            new ThrowingConfigurationService(),
            new FakePowerService(),
            new RecordingWin32());

        var result = await gate.ExecuteAsync(Request(confirmed: true), CancellationToken.None);

        Assert.Equal(PowerOutcome.Rejected, result.Outcome);
    }

    // ---- Helpers ----

    private static AppConfig Config(bool testMode, bool realPowerEnabled) => new()
    {
        SchemaVersion = 1,
        TestMode = testMode,
        RealPowerEnabled = realPowerEnabled,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static PowerRequest Request(bool confirmed) => new()
    {
        Action = PowerAction.Shutdown,
        InstanceId = InstanceId,
        Reason = "test",
        RealPowerConfirmed = confirmed
    };

    private static (GuardedPowerService Gate, FakePowerService Fake, RecordingWin32 Real) CreateGate(AppConfig config)
    {
        var fake = new FakePowerService();
        var real = new RecordingWin32();
        var gate = new GuardedPowerService(
            new StubConfigurationService(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = config
            }),
            fake,
            real);
        return (gate, fake, real);
    }

    private sealed class StubConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public StubConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingConfigurationService : IConfigurationService
    {
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("config failure");

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingWin32 : IPowerService
    {
        public int CallCount { get; private set; }

        public Task<PowerResult> ExecuteAsync(PowerRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new PowerResult
            {
                Outcome = PowerOutcome.Accepted,
                Message = "recorded win32"
            });
        }
    }
}
