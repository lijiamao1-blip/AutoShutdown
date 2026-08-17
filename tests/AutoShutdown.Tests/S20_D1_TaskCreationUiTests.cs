using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Unattended;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S20-D1 任务级「使用无人值守」选择（UI/任务创建切片）。
/// 验证两条创建路径：人工确认（RealPowerConfirmed=true）与显式无人值守（RealPowerConfirmed=false
/// + UseUnattended=true），以及「使用无人值守」选项的可见性/可选性闸门（默认关闭、授权失效、
/// 动作不匹配、策略异常一律不可选）。纯内存，无真实电源、无 MessageBox。
/// </summary>
public sealed class S20_D1_TaskCreationUiTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    // ---- (a) 人工确认路径 ----

    [Fact]
    public async Task ManualPath_RealPower_WithConfirmation_BuildsConfirmedDefinition()
    {
        var confirmCalls = 0;
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            realPowerConfirmation: () =>
            {
                confirmCalls++;
                return true;
            });
        await viewModel.RefreshConfigurationAsync();

        Assert.True(viewModel.IsUnattendedTaskOptionVisible);
        Assert.False(viewModel.IsUnattendedTaskOptionAvailable); // 无授权服务 → 不可选

        viewModel.SelectedMode = TimeMode.Countdown;
        viewModel.UseUnattended = false;

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.True(definition.RealPowerConfirmed);
        Assert.False(definition.UseUnattended);
        Assert.Equal(1, confirmCalls);
    }

    [Fact]
    public async Task ManualPath_RealPower_ConfirmationDeclined_RejectsDefinition()
    {
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            realPowerConfirmation: () => false);
        await viewModel.RefreshConfigurationAsync();

        viewModel.UseUnattended = false;

        Assert.False(viewModel.TryBuildDefinition(out _, out var error));
        Assert.Contains("已取消创建真实电源任务", error);
    }

    // ---- (b) 显式无人值守路径 ----

    [Fact]
    public async Task UnattendedPath_ValidAuthorization_BuildsUnattendedDefinition_WithoutManualConfirmation()
    {
        var confirmCalls = 0;
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            realPowerConfirmation: () =>
            {
                confirmCalls++;
                return true;
            },
            policy: new FixedUnattendedPolicyService(action => Authorized(action)));
        await viewModel.RefreshConfigurationAsync();

        Assert.True(viewModel.IsUnattendedTaskOptionVisible);
        Assert.True(viewModel.IsUnattendedTaskOptionAvailable);

        viewModel.UseUnattended = true;

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.False(definition.RealPowerConfirmed);
        Assert.True(definition.UseUnattended);
        Assert.Equal(0, confirmCalls); // 不调用人工确认
    }

    // ---- (c) 选项可见性 / 可选性闸门 ----

    [Fact]
    public async Task UnattendedOption_TestMode_NotVisible()
    {
        var viewModel = CreateViewModel(TestModeConfig());
        await viewModel.RefreshConfigurationAsync();

        Assert.False(viewModel.IsUnattendedTaskOptionVisible);
        Assert.False(viewModel.IsUnattendedTaskOptionAvailable);
    }

    [Fact]
    public async Task UnattendedOption_ExpiredAuthorization_NotAvailable()
    {
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            policy: new FixedUnattendedPolicyService(_ =>
                UnattendedAuthorizationDecision.Denied(UnattendedPolicyStatus.Expired, "expired")));
        await viewModel.RefreshConfigurationAsync();

        Assert.True(viewModel.IsUnattendedTaskOptionVisible);
        Assert.False(viewModel.IsUnattendedTaskOptionAvailable);

        // 即便用户强行置真，构建也必须 fail-closed（不绕过人工确认）。
        viewModel.UseUnattended = true;
        Assert.False(viewModel.TryBuildDefinition(out _, out var error));
        Assert.Contains("无人值守授权无效", error);
    }

    [Fact]
    public async Task UnattendedOption_ActionMismatch_NotAvailable()
    {
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            policy: new FixedUnattendedPolicyService(action =>
                action == PowerAction.Shutdown
                    ? Authorized(PowerAction.Shutdown)
                    : UnattendedAuthorizationDecision.Denied(UnattendedPolicyStatus.ActionMismatch, "mismatch")));
        viewModel.SelectedAction = PowerAction.Hibernate;
        await viewModel.RefreshConfigurationAsync();

        Assert.True(viewModel.IsUnattendedTaskOptionVisible);
        Assert.False(viewModel.IsUnattendedTaskOptionAvailable);

        viewModel.UseUnattended = true;
        Assert.False(viewModel.TryBuildDefinition(out _, out var error));
        Assert.Contains("无人值守授权无效", error);
    }

    [Fact]
    public async Task UnattendedOption_PolicyThrows_NotAvailable_NoException()
    {
        var viewModel = CreateViewModel(
            RealPowerConfig(),
            policy: new FixedUnattendedPolicyService(
                _ => throw new InvalidOperationException("storage failure")));
        await viewModel.RefreshConfigurationAsync();

        Assert.True(viewModel.IsUnattendedTaskOptionVisible);
        Assert.False(viewModel.IsUnattendedTaskOptionAvailable);

        viewModel.UseUnattended = true;
        Assert.False(viewModel.TryBuildDefinition(out _, out _));
    }

    // ---- 夹具 ----

    private static MainWindowViewModel CreateViewModel(
        AppConfig config,
        Func<bool>? realPowerConfirmation = null,
        IUnattendedPolicyService? policy = null) => new(
            new FakeSchedulerEngine(),
            new RecordingConfigurationService(config),
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService(),
            realPowerConfirmation: realPowerConfirmation,
            unattendedPolicy: policy);

    private static AppConfig RealPowerConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static AppConfig TestModeConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static UnattendedAuthorizationDecision Authorized(PowerAction action)
        => UnattendedAuthorizationDecision.Granted(new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 3,
            AuthorizedAtUtc = Now,
            AuthorizedAction = action,
            TriggerReason = "nightly"
        });

    private sealed class FixedUnattendedPolicyService : IUnattendedPolicyService
    {
        private readonly Func<PowerAction, UnattendedAuthorizationDecision> _evaluate;

        public FixedUnattendedPolicyService(Func<PowerAction, UnattendedAuthorizationDecision> evaluate)
            => _evaluate = evaluate;

        public Task<UnattendedAuthorizationDecision> EvaluateAsync(
            PowerAction action,
            CancellationToken cancellationToken) => Task.FromResult(_evaluate(action));

        public Task<UnattendedEnableResult> EnableAsync(
            UnattendedEnableRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UnattendedRevokeResult> RevokeAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public RecordingConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public SchedulerSnapshot GetSnapshot() => Snapshot;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
            => Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class NoOpAutoStartService : IAutoStartService
    {
        public AutoStartStatus GetStatus() => AutoStartStatus.Disabled;

        public AutoStartOperationResult Enable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);

        public AutoStartOperationResult Disable()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Disabled);

        public AutoStartOperationResult Repair()
            => AutoStartOperationResult.Success("OK", "ok", AutoStartStatus.Enabled);
    }

    private sealed class NullLogger : IApplicationLogger
    {
        public string LogDirectory => "C:\\logs";

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
        {
        }

        public void Dispose()
        {
        }
    }
}
