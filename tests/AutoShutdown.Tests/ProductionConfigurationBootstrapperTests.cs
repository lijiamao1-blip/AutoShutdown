using AutoShutdown.App.Infrastructure;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class ProductionConfigurationBootstrapperTests
{
    [Fact]
    public void RealPowerBootstrap_OnBlockedUiSynchronizationContext_DoesNotDeadlock()
    {
        var root = Path.Combine(Path.GetTempPath(), "autoshutdown-production-bootstrap-" + Guid.NewGuid().ToString("N"));
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new ThrowingSynchronizationContext());
            var service = new ConfigurationService(new FileStorage(root));

            var result = BlockingBootstrap(service);

            Assert.True(result.Succeeded);
            Assert.True(result.Changed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task MissingConfig_CreatesRealPowerDefault()
    {
        var service = new RecordingConfigurationService(Missing());

        var result = await ProductionConfigurationBootstrapper.EnsureRealPowerModeAsync(
            service, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.False(service.Saved!.TestMode);
        Assert.True(service.Saved.RealPowerEnabled);
        Assert.False(service.Saved.StartWithWindows);
        Assert.Equal(4, service.Saved.AllowedActions.Length);
    }

    [Fact]
    public async Task ExistingTestConfig_IsSwitchedToRealPower_AndOtherSettingsArePreserved()
    {
        var existing = ProductionConfig() with
        {
            TestMode = true,
            RealPowerEnabled = false,
            DefaultWarningSeconds = 37
        };
        var service = new RecordingConfigurationService(Success(existing));

        var result = await ProductionConfigurationBootstrapper.EnsureRealPowerModeAsync(
            service, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(service.Saved!.TestMode);
        Assert.True(service.Saved.RealPowerEnabled);
        Assert.Equal(37, service.Saved.DefaultWarningSeconds);
    }

    [Fact]
    public async Task CorruptConfig_IsNotOverwritten()
    {
        var service = new RecordingConfigurationService(new ConfigurationLoadResult
        {
            Status = ConfigurationLoadStatus.Corrupt
        });

        var result = await ProductionConfigurationBootstrapper.EnsureRealPowerModeAsync(
            service, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(service.Saved);
    }

    private static AppConfig ProductionConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static ConfigurationLoadResult Missing() => new() { Status = ConfigurationLoadStatus.Missing };
    private static ProductionBootstrapResult BlockingBootstrap(IConfigurationService service)
        => ProductionConfigurationBootstrapper
            .EnsureRealPowerModeAsync(service, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static ConfigurationLoadResult Success(AppConfig config) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = config
    };

    private sealed class RecordingConfigurationService(ConfigurationLoadResult load) : IConfigurationService
    {
        public AppConfig? Saved { get; private set; }
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(load);
        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
        {
            Saved = config;
            return Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
        }
        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("Continuation posted to blocked UI context.");

        public override void Send(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("Continuation sent to blocked UI context.");
    }
}
