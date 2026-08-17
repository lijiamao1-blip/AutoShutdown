using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S21-C3：WoL 调度任务类型。结构校验：WoL 必填 TargetMachineId 且禁止 RtcWakeTimeUtc，
/// 电源动作禁止 TargetMachineId；实例快照携带 TargetMachineId/RtcWakeTimeUtc；
/// ShutdownWorkflow 将实例的 RTC 唤醒时间填入 Pre-Pipeline 上下文（受控关机前步骤）。
/// </summary>
public sealed class S21_WoLTaskDefinitionTests
{
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TargetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset WakeTime =
        new(2024, 1, 16, 7, 0, 0, TimeSpan.Zero);

    // ===== 结构校验（经 TaskService.Create 调用 TaskDefinitionValidator） =====

    [Fact]
    public void Create_WhenWakeOnLanMissingTargetMachineId_ReturnsInvalidDefinition()
    {
        var service = CreateService();

        var result = service.Create(WolDefinition(targetId: null), Now, TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.InvalidDefinition, result.Status);
        Assert.Null(result.Instance);
    }

    [Fact]
    public void Create_WhenWakeOnLanWithEmptyTargetMachineId_ReturnsInvalidDefinition()
    {
        var service = CreateService();

        var result = service.Create(WolDefinition(targetId: Guid.Empty), Now, TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.InvalidDefinition, result.Status);
    }

    [Fact]
    public void Create_WhenWakeOnLanWithRtcWakeTime_ReturnsInvalidDefinition()
    {
        var service = CreateService();

        var result = service.Create(
            WolDefinition(targetId: TargetId) with { RtcWakeTimeUtc = WakeTime },
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.InvalidDefinition, result.Status);
        Assert.Null(result.Instance);
    }

    [Fact]
    public void Create_WhenPowerActionWithTargetMachineId_ReturnsInvalidDefinition()
    {
        var service = CreateService();

        var result = service.Create(
            PowerDefinition() with { TargetMachineId = TargetId },
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.InvalidDefinition, result.Status);
        Assert.Null(result.Instance);
    }

    [Fact]
    public void Create_WhenWakeOnLanWithTargetMachineId_IsValid()
    {
        var service = CreateService();

        var result = service.Create(WolDefinition(targetId: TargetId), Now, TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(PowerAction.WakeOnLan, result.Instance!.ActionSnapshot);
        Assert.Equal(TargetId, result.Instance.TargetMachineId);
        Assert.Null(result.Instance.RtcWakeTimeUtc);
    }

    // ===== 实例快照复制 =====

    [Fact]
    public void Create_PowerTaskWithRtcWakeTime_CopiesRtcWakeTimeIntoInstance()
    {
        var service = CreateService();

        var result = service.Create(
            PowerDefinition() with { RtcWakeTimeUtc = WakeTime },
            Now,
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(WakeTime, result.Instance!.RtcWakeTimeUtc);
        Assert.Null(result.Instance.TargetMachineId);
    }

    // ===== ShutdownWorkflow 将实例 RTC 唤醒时间填入 Pre-Pipeline 上下文 =====

    [Fact]
    public async Task Execute_PassesInstanceRtcWakeTimeIntoPipelineContext()
    {
        var recording = new RecordingPipelineRunner();
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(ConfigResult(testMode: true)),
            new FixedPowerService(PowerResultSimulated()),
            recording);

        await workflow.ExecuteAsync(ValidPowerInstance(wakeTime: WakeTime), CancellationToken.None);

        Assert.NotNull(recording.Context);
        Assert.Equal(WakeTime, recording.Context!.RtcWakeTimeUtc);
    }

    [Fact]
    public async Task Execute_WhenInstanceHasNoRtcWakeTime_ContextWakeTimeIsNull()
    {
        var recording = new RecordingPipelineRunner();
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(ConfigResult(testMode: true)),
            new FixedPowerService(PowerResultSimulated()),
            recording);

        await workflow.ExecuteAsync(ValidPowerInstance(wakeTime: null), CancellationToken.None);

        Assert.NotNull(recording.Context);
        Assert.Null(recording.Context!.RtcWakeTimeUtc);
    }

    private static TaskService CreateService()
        => new(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(InstanceId, StageToken));

    private static TaskDefinition WolDefinition(Guid? targetId) => new()
    {
        Id = SourceTaskId,
        Kind = TaskKind.DailyAt,
        Action = PowerAction.WakeOnLan,
        TargetTimeOfDay = new TimeOnly(15, 0),
        TargetMachineId = targetId,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static TaskDefinition PowerDefinition() => new()
    {
        Id = SourceTaskId,
        Kind = TaskKind.DailyAt,
        Action = PowerAction.Shutdown,
        TargetTimeOfDay = new TimeOnly(15, 0),
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static ConfigurationLoadResult ConfigResult(bool testMode) => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = new AppConfig
        {
            SchemaVersion = 1,
            TestMode = testMode,
            AllowedActions = [PowerAction.Shutdown]
        }
    };

    private static PowerResult PowerResultSimulated() => new()
    {
        Outcome = PowerOutcome.Simulated,
        Message = "simulated"
    };

    private static TaskInstance ValidPowerInstance(DateTimeOffset? wakeTime) => new()
    {
        InstanceId = InstanceId,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskInstanceState.Executing,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        StageToken = StageToken,
        HasExecuted = true,
        RtcWakeTimeUtc = wakeTime,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private sealed class RecordingPipelineRunner : IPrePipelineRunner
    {
        public PrePipelineContext? Context { get; private set; }

        public Task<PrePipelineRunResult> RunAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            Context = context;
            return Task.FromResult(new PrePipelineRunResult
            {
                Status = PrePipelineRunStatus.Completed,
                Actions = []
            });
        }
    }

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public FixedConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(
            AppConfig config,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedPowerService : IPowerService
    {
        private readonly PowerResult _result;

        public FixedPowerService(PowerResult result) => _result = result;

        public Task<PowerResult> ExecuteAsync(
            PowerRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }

    private sealed class SequentialIdentifierGenerator : IIdentifierGenerator
    {
        private readonly Queue<Guid> _values;

        public SequentialIdentifierGenerator(params Guid[] values) => _values = new Queue<Guid>(values);

        public Guid NewId()
        {
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("No more identifiers configured.");
            }

            return _values.Dequeue();
        }
    }
}
