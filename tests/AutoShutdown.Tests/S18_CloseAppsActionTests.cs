using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18 C3 CloseAppsAction 动作层单元测试。验证动作契约（名称/默认 Block 策略）、上下文校验、
/// 报告到 PrePipelineActionResult 的映射（成功清空错误文本、失败携带脱敏摘要）、取消传播。
/// 通过 CloseAppsService + 进程/窗口/配置替身验证，绝不触碰真实进程。
/// </summary>
public sealed class S18_CloseAppsActionTests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    [Fact]
    public void Defaults_NameIsCloseApps_AndPolicyIsBlock()
    {
        var action = new CloseAppsAction(Service(new CloseAppsConfig { Targets = [] }));

        Assert.Equal("CloseApps", action.Name);
        Assert.Equal(FailurePolicy.Block, action.FailurePolicy);
    }

    [Fact]
    public void ContinuePolicyVariant_ExposesContinue()
    {
        var action = new CloseAppsAction(
            Service(new CloseAppsConfig { Targets = [] }),
            FailurePolicy.Continue);

        Assert.Equal(FailurePolicy.Continue, action.FailurePolicy);
    }

    [Fact]
    public async Task Success_MapsToSucceededWithEmptyError()
    {
        var action = new CloseAppsAction(Service(new CloseAppsConfig { Targets = [] }));

        var result = await action.ExecuteAsync(Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.ErrorMessage);
    }

    [Fact]
    public async Task Failure_MapsSummaryToErrorMessage()
    {
        var action = new CloseAppsAction(Service(
            new CloseAppsConfig
            {
                GracefulTimeoutSeconds = 1,
                Targets = [new CloseAppsTargetConfig { ExecutablePath = NotepadPath }]
            },
            NotepadProcess()));

        var result = await action.ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("notepad.exe: timed out", result.ErrorMessage);
    }

    [Fact]
    public void NullService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CloseAppsAction(null!));
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        var action = new CloseAppsAction(Service(new CloseAppsConfig { Targets = [] }));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var action = new CloseAppsAction(Service(
            new CloseAppsConfig
            {
                GracefulTimeoutSeconds = 1,
                Targets = [new CloseAppsTargetConfig { ExecutablePath = NotepadPath }]
            },
            NotepadProcess()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => action.ExecuteAsync(Context(), cts.Token));
    }

    private static CloseAppsService Service(CloseAppsConfig closeApps, ProcessSnapshot? process = null)
        => new(
            new FakeConfigurationService(ConfigWith(closeApps)),
            new FakeProcessManager(process),
            new FakeAppWindowManager());

    private static AppConfig ConfigWith(CloseAppsConfig closeApps) => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        CloseApps = closeApps
    };

    private static PrePipelineContext Context() => new()
    {
        InstanceId = Guid.NewGuid(),
        SourceTaskId = Guid.NewGuid(),
        Action = PowerAction.Shutdown,
        ScheduledFireTime = DateTimeOffset.UtcNow
    };

    private static ProcessSnapshot NotepadProcess() => new()
    {
        ProcessId = 100,
        ProcessName = "notepad.exe",
        ExecutablePath = NotepadPath,
        SessionId = 1,
        StartTimeUtc = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero)
    };

    private sealed class FakeConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public FakeConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeProcessManager : IProcessManager
    {
        private readonly ProcessSnapshot? _single;

        public FakeProcessManager(ProcessSnapshot? single) => _single = single;

        public int CurrentProcessId => 1000;
        public int CurrentSessionId => 1;

        public IReadOnlyList<ProcessSnapshot> EnumerateProcesses()
            => _single is null ? [] : [_single];

        public ProcessSnapshot? GetProcessById(int processId)
            => _single?.ProcessId == processId ? _single : null;
    }

    private sealed class FakeAppWindowManager : IAppWindowManager
    {
        public bool HasMainWindow(int processId) => true;
        public bool RequestClose(int processId) => true;
        public ProcessExitStatus GetExitStatus(int processId) => ProcessExitStatus.Running;

        public bool WaitForExit(int processId, TimeSpan timeout, CancellationToken cancellationToken) => false; // 总是超时（未授权强杀）。

        public ForceKillResult ForceKill(int processId, DateTimeOffset expectedStartTimeUtc)
            => throw new InvalidOperationException("ForceKill must not be reached without authorization.");
    }
}
