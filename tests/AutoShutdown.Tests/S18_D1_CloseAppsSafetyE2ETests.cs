using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.CloseApps;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18-D1 返修端到端安全测试。把真实 <see cref="CloseAppsAction"/>（默认 Block）接入真实
/// <see cref="PrePipelineRunner"/> 与 <see cref="ShutdownWorkflow"/>，用记录电源替身验证：
/// 关闭应用安全失败（未知会话 / 退出状态无法确认 / 强杀未确认）时，流水线阻断、绝不触发电源
/// （power=0）。全程 Fake 进程/窗口/配置/电源，绝不触碰真实进程或真实电源。
/// </summary>
public sealed class S18_D1_CloseAppsSafetyE2ETests
{
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    private static readonly DateTimeOffset StartTime =
        new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnknownSession_BlocksPipeline_AndPowerZero()
    {
        // D1-2：SessionId == -1（未知）→ SkippedProtected → Block → power=0。
        var (result, requests) = await RunCloseAppsAsync(
            Process(100, sessionId: -1),
            new FakeWindowManager());

        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task ExitStatusUnknown_BlocksPipeline_AndPowerZero()
    {
        // D1-3：退出状态无法确认 → ExitStatusUnknown → Block → power=0。
        var (result, requests) = await RunCloseAppsAsync(
            Process(100, sessionId: 1),
            new FakeWindowManager { ExitStatus = ProcessExitStatus.Unknown });

        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task ForceKillExitNotConfirmed_BlocksPipeline_AndPowerZero()
    {
        // D1-4：强杀后退出未确认 → ExitNotConfirmed → Block → power=0。
        var (result, requests) = await RunCloseAppsAsync(
            Process(100, sessionId: 1),
            new FakeWindowManager
            {
                MainWindowPresent = false,
                ForceKillStatus = ForceKillStatus.ExitNotConfirmed
            });

        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        Assert.Empty(requests);
    }

    private static async Task<(ShutdownWorkflowResult Result, IReadOnlyList<PowerRequest> Requests)>
        RunCloseAppsAsync(ProcessSnapshot process, FakeWindowManager window)
    {
        var closeAppsService = new CloseAppsService(
            new FakeConfigurationService(ConfigWithCloseApps()),
            new FakeProcessManager(process),
            window);
        var action = new CloseAppsAction(closeAppsService);
        var runner = new PrePipelineRunner([action]);
        var power = new RecordingPowerService();
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(ValidConfig()),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);
        return (result, power.Requests);
    }

    private static AppConfig ConfigWithCloseApps() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 },
        CloseApps = new CloseAppsConfig
        {
            GracefulTimeoutSeconds = 5,
            Targets = [new CloseAppsTargetConfig { ExecutablePath = NotepadPath, ForceKillAllowed = true }]
        }
    };

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskInstance ValidInstance() => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = StartTime,
        WarningStartTime = null,
        HasExecuted = true,
        CreatedAt = StartTime.AddHours(-1)
    };

    private static ProcessSnapshot Process(int pid, int sessionId) => new()
    {
        ProcessId = pid,
        ProcessName = "notepad.exe",
        ExecutablePath = NotepadPath,
        SessionId = sessionId,
        StartTimeUtc = StartTime
    };

    private sealed class FakeProcessManager : IProcessManager
    {
        private readonly ProcessSnapshot _process;

        public FakeProcessManager(ProcessSnapshot process) => _process = process;

        public int CurrentProcessId => 1000;
        public int CurrentSessionId => 1;

        public IReadOnlyList<ProcessSnapshot> EnumerateProcesses() => [_process];

        public ProcessSnapshot? GetProcessById(int processId)
            => _process.ProcessId == processId ? _process : null;
    }

    private sealed class FakeWindowManager : IAppWindowManager
    {
        public bool MainWindowPresent { get; init; } = true;
        public ProcessExitStatus ExitStatus { get; init; } = ProcessExitStatus.Running;
        public ForceKillStatus ForceKillStatus { get; init; } = ForceKillStatus.Killed;

        public bool HasMainWindow(int processId) => MainWindowPresent;

        public bool RequestClose(int processId) => true;

        public ProcessExitStatus GetExitStatus(int processId) => ExitStatus;

        public bool WaitForExit(int processId, TimeSpan timeout, CancellationToken cancellationToken) => false;

        public ForceKillResult ForceKill(int processId, DateTimeOffset expectedStartTimeUtc)
            => new() { Status = ForceKillStatus };
    }

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

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public FixedConfigurationService(AppConfig config) => _config = config;

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

    private sealed class RecordingPowerService : IPowerService
    {
        public List<PowerRequest> Requests { get; } = [];

        public Task<PowerResult> ExecuteAsync(PowerRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new PowerResult
            {
                Outcome = PowerOutcome.Simulated,
                WasSimulated = true,
                Message = "simulated"
            });
        }
    }
}
