using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Office;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S17 Workflow↔OfficeSaveAction 集成测试。验证 OfficeSave 作为第一个 Action 接入
/// S16 冻结流水线后的次序与电源语义：成功/Continue 失败放行电源、Block 失败阻断且
/// IPowerService 调用 0 次。全程使用替身电源，绝不触发真实关机/重启/睡眠/休眠。
/// </summary>
public sealed class S17_OfficeIntegrationTests
{
    private static readonly Guid InstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceTaskId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid StageToken = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact(Timeout = 2000)]
    public async Task OfficeSave_Success_PowerRunsOnce()
    {
        var automation = new FakeOfficeAutomation(SuccessResult());
        var runner = new PrePipelineRunner([new OfficeSaveAction(automation)]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.PrePipeline);
        Assert.Single(result.PrePipeline.Actions);
        Assert.Equal("OfficeSave", result.PrePipeline.Actions[0].ActionName);
        Assert.True(result.PrePipeline.Actions[0].Succeeded);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task OfficeSave_ContinueFailure_AuditPreserved_PowerRuns()
    {
        var automation = new FakeOfficeAutomation(PartialFailureResult());
        // 默认 Continue：失败保留审计，但后续仍放行电源。
        var runner = new PrePipelineRunner([new OfficeSaveAction(automation)]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.PrePipeline);
        Assert.False(result.PrePipeline.Actions[0].Succeeded);
        Assert.Equal("Word: 1 no-path, 0 failed", result.PrePipeline.Actions[0].ErrorMessage);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task OfficeSave_BlockFailure_PowerNotCalled()
    {
        var automation = new FakeOfficeAutomation(PartialFailureResult());
        var runner = new PrePipelineRunner([
            new OfficeSaveAction(automation, failurePolicy: FailurePolicy.Block)
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDecisionCode.PrePipelineBlocked, result.DecisionCode);
        Assert.NotNull(result.PrePipeline);
        Assert.Equal(PrePipelineRunStatus.Blocked, result.PrePipeline.Status);
        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task OfficeSave_RegisteredFirst_RunsBeforeSubsequentActions()
    {
        var log = new List<string>();
        var automation = new FakeOfficeAutomation(SuccessResult(), () => log.Add("OfficeSave"));
        var runner = new PrePipelineRunner(
        [
            new OfficeSaveAction(automation),
            new NamedAction("RunCommands", log),
            new NamedAction("CloseApps", log)
        ]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        var result = await workflow.ExecuteAsync(ValidInstance(), CancellationToken.None);

        Assert.True(result.Succeeded);
        // 固定顺序：OfficeSave 排在最前。
        Assert.Equal(["OfficeSave", "RunCommands", "CloseApps"], log);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task OfficeSave_CleanupFault_PropagatesOce_PowerNotCalled()
    {
        // D5：内部超时清理安全故障（OCE 子类）从 Runner/Workflow 原样传播，
        // 终止整个工作流，唯一电源出口调用 0 次（绝不 Block 后继续或降级为 Continue）。
        var automation = new CleanupFaultAutomation();
        var runner = new PrePipelineRunner([new OfficeSaveAction(automation)]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        await Assert.ThrowsAsync<OfficeHelperCleanupFailedException>(
            () => workflow.ExecuteAsync(ValidInstance(), CancellationToken.None));

        Assert.Empty(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task OfficeSave_ExternalCancel_PropagatesOce_PowerNotCalled()
    {
        // D5：外部取消仍传播普通 OCE（绝不转 NotDetected/Continue），唯一电源出口调用 0 次。
        var automation = new FakeOfficeAutomation(SuccessResult());
        var runner = new PrePipelineRunner([new OfficeSaveAction(automation)]);
        var power = new RecordingPowerService(SimulatedResult());
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(SuccessResult(ValidConfig())),
            power,
            runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => workflow.ExecuteAsync(ValidInstance(), new CancellationToken(canceled: true)));

        Assert.Empty(power.Requests);
    }

    private static ConfigurationLoadResult SuccessResult(AppConfig config) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = config
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
        InstanceId = InstanceId,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = StageToken,
        HasExecuted = true,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static PowerResult SimulatedResult() => new()
    {
        Outcome = PowerOutcome.Simulated,
        WasSimulated = true,
        Message = "simulated"
    };

    private static OfficeApplicationSaveResult SuccessResult() => new()
    {
        Application = OfficeApplicationKind.Word,
        Status = OfficeAppStatus.Success,
        SavedCount = 1
    };

    private static OfficeApplicationSaveResult PartialFailureResult() => new()
    {
        Application = OfficeApplicationKind.Word,
        Status = OfficeAppStatus.PartialFailure,
        NoPathCount = 1
    };

    private sealed class FakeOfficeAutomation : IOfficeAutomation
    {
        private readonly OfficeApplicationSaveResult _result;
        private readonly Action? _onSave;

        public FakeOfficeAutomation(OfficeApplicationSaveResult result, Action? onSave = null)
        {
            _result = result;
            _onSave = onSave;
        }

        public IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications()
            => [OfficeApplicationKind.Word];

        public OfficeApplicationSaveResult SaveOpenDocuments(
            OfficeApplicationKind application,
            CancellationToken cancellationToken)
        {
            _onSave?.Invoke();
            return _result;
        }
    }

    /// <summary>内部超时清理安全故障替身：SaveOpenDocuments 抛 OCE 子类安全故障。</summary>
    private sealed class CleanupFaultAutomation : IOfficeAutomation
    {
        public IReadOnlyList<OfficeApplicationKind> DetectAvailableApplications()
            => [OfficeApplicationKind.Word];

        public OfficeApplicationSaveResult SaveOpenDocuments(
            OfficeApplicationKind application,
            CancellationToken cancellationToken)
            => throw new OfficeHelperCleanupFailedException("helper cleanup failed");
    }

    private sealed class NamedAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly List<string> _log;

        public NamedAction(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => FailurePolicy.Continue;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            _log.Add(_name);
            return Task.FromResult(new PrePipelineActionResult { Succeeded = true });
        }
    }

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public FixedConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPowerService : IPowerService
    {
        private readonly List<PowerRequest> _requests = [];
        private readonly PowerResult _result;

        public RecordingPowerService(PowerResult result) => _result = result;

        public IReadOnlyList<PowerRequest> Requests => _requests;

        public Task<PowerResult> ExecuteAsync(
            PowerRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_result);
        }
    }
}
