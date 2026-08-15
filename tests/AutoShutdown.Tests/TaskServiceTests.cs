using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class TaskServiceTests
{
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid AnotherTaskId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid StageToken2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid StageToken3 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WhenValid_ReturnsInstanceWithAllFieldsAndCallsGeneratorTwice()
    {
        var generator = new SequentialIdentifierGenerator(InstanceId1, StageToken1);
        var service = CreateService(generator: generator);

        var result = service.Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(2, generator.CallCount);
        var instance = result.Instance!;
        Assert.Equal(InstanceId1, instance.InstanceId);
        Assert.Equal(SourceTaskId, instance.SourceTaskId);
        Assert.Equal(PowerAction.Shutdown, instance.ActionSnapshot);
        Assert.Equal(TaskState.Scheduled, instance.State);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero), instance.ScheduledFireTime);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 11, 59, 0, TimeSpan.Zero), instance.WarningStartTime);
        Assert.Equal(StageToken1, instance.StageToken);
        Assert.False(instance.HasExecuted);
        Assert.Equal(Now.ToUniversalTime(), instance.CreatedAt);
    }

    [Fact]
    public void Create_GeneratedGuidsAreNonEmptyAndDistinct()
    {
        var generator = new SequentialIdentifierGenerator(InstanceId1, StageToken1);
        var service = CreateService(generator: generator);

        var result = service.Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.NotEqual(Guid.Empty, result.Instance!.InstanceId);
        Assert.NotEqual(Guid.Empty, result.Instance.StageToken);
        Assert.NotEqual(result.Instance.InstanceId, result.Instance.StageToken);
    }

    [Fact]
    public void Create_WhenDefinitionIsInvalid_ReturnsInvalidDefinitionWithoutCallingGenerator()
    {
        var generator = new SequentialIdentifierGenerator(InstanceId1, StageToken1);
        var service = CreateService(generator: generator);

        var emptyId = service.Create(CountdownDefinition() with { Id = Guid.Empty }, Now, TimeZoneInfo.Utc);
        var unknownAction = service.Create(CountdownDefinition() with { Action = PowerAction.Unknown }, Now, TimeZoneInfo.Utc);
        var undefinedAction = service.Create(CountdownDefinition() with { Action = (PowerAction)99 }, Now, TimeZoneInfo.Utc);
        var negativeWarning = service.Create(CountdownDefinition() with { WarningSeconds = -1 }, Now, TimeZoneInfo.Utc);
        var tooLargeWarning = service.Create(CountdownDefinition() with { WarningSeconds = 86401 }, Now, TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.InvalidDefinition, emptyId.Status);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, unknownAction.Status);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, undefinedAction.Status);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, negativeWarning.Status);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, tooLargeWarning.Status);
        Assert.All(
            new[] { emptyId, unknownAction, undefinedAction, negativeWarning, tooLargeWarning },
            result => Assert.Null(result.Instance));
        Assert.Equal(0, generator.CallCount);
    }

    [Theory]
    [InlineData(NextExecutionStatus.NoFutureOccurrence)]
    [InlineData(NextExecutionStatus.InvalidTaskKind)]
    [InlineData(NextExecutionStatus.InvalidLocalTime)]
    public void Create_WhenScheduleCalculationFails_ReturnsScheduleCalculationFailedWithStatus(
        NextExecutionStatus scheduleStatus)
    {
        var calculator = new FixedResultCalculator(
            new NextExecutionResult { Status = scheduleStatus, Message = "failed" });
        var generator = new SequentialIdentifierGenerator(InstanceId1, StageToken1);
        var service = CreateService(calculator: calculator, generator: generator);

        var result = service.Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.ScheduleCalculationFailed, result.Status);
        Assert.Equal(scheduleStatus, result.ScheduleStatus);
        Assert.Null(result.Instance);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void Create_WarningStartTime_IsComputedClampedAndNormalized()
    {
        var normal = CreateService().Create(
            CountdownDefinition(warningSeconds: 60), Now, TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 11, 59, 0, TimeSpan.Zero), normal.Instance!.WarningStartTime);

        var zero = CreateService().Create(
            CountdownDefinition(warningSeconds: 0), Now, TimeZoneInfo.Utc);
        Assert.Null(zero.Instance!.WarningStartTime);

        var missing = CreateService().Create(
            CountdownDefinition(warningSeconds: null), Now, TimeZoneInfo.Utc);
        Assert.Null(missing.Instance!.WarningStartTime);

        var clamped = CreateService().Create(
            CountdownDefinition(warningSeconds: 7200), Now, TimeZoneInfo.Utc);
        Assert.Equal(Now.ToUniversalTime(), clamped.Instance!.WarningStartTime);
    }

    [Fact]
    public void Create_WhenStateMachineRejects_ReturnsTransitionRejectedWithoutInstanceOrGuid()
    {
        var machine = new RejectingStateMachine();
        var generator = new SequentialIdentifierGenerator(InstanceId1, StageToken1);
        var service = CreateService(machine: machine, generator: generator);

        var result = service.Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.TransitionRejected, result.Status);
        Assert.Equal(TaskTransitionDecisionCode.TransitionNotAllowed, result.TransitionDecisionCode);
        Assert.Null(result.Instance);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void Snooze_FromWarning_ReturnsScheduledWithNewTokenAndTime()
    {
        var current = WarningInstance();
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(generator: generator);

        var result = service.Snooze(current, TimeSpan.FromMinutes(10), Now);

        Assert.True(result.Succeeded);
        var instance = result.Instance!;
        Assert.Equal(TaskState.Scheduled, instance.State);
        Assert.Equal(Now.AddMinutes(10).ToUniversalTime(), instance.ScheduledFireTime);
        Assert.Equal(StageToken3, instance.StageToken);
        Assert.NotEqual(current.StageToken, instance.StageToken);
        Assert.Null(instance.WarningStartTime);
        Assert.False(instance.HasExecuted);
        Assert.Equal(current.InstanceId, instance.InstanceId);
        Assert.Equal(current.SourceTaskId, instance.SourceTaskId);
        Assert.Equal(current.ActionSnapshot, instance.ActionSnapshot);
        Assert.Equal(current.CreatedAt, instance.CreatedAt);
    }

    [Fact]
    public void Snooze_FromScheduled_UsesRescheduleCause()
    {
        var current = ScheduledInstance();
        var machine = new RecordingStateMachine();
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(machine: machine, generator: generator);

        var result = service.Snooze(current, TimeSpan.FromMinutes(5), Now);

        Assert.True(result.Succeeded);
        Assert.Equal(TaskState.Scheduled, result.Instance!.State);
        Assert.Contains(
            (TaskState.Scheduled, TaskState.Scheduled, TaskTransitionCause.Reschedule),
            machine.Calls);
    }

    [Fact]
    public void Snooze_WhenDurationIsInvalid_ReturnsInvalidDuration()
    {
        var service = CreateService();

        var zero = service.Snooze(ScheduledInstance(), TimeSpan.Zero, Now);
        var negative = service.Snooze(ScheduledInstance(), TimeSpan.FromHours(-1), Now);
        var tooLong = service.Snooze(ScheduledInstance(), TimeSpan.FromDays(8), Now);

        Assert.Equal(TaskCommandStatus.InvalidDuration, zero.Status);
        Assert.Equal(TaskCommandStatus.InvalidDuration, negative.Status);
        Assert.Equal(TaskCommandStatus.InvalidDuration, tooLong.Status);
    }

    [Fact]
    public void Snooze_SevenDaysIsAllowed()
    {
        var service = CreateService(
            generator: new SequentialIdentifierGenerator(StageToken3));

        var result = service.Snooze(ScheduledInstance(), TimeSpan.FromDays(7), Now);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(TaskState.Idle, TaskTransitionDecisionCode.CauseMismatch)]
    [InlineData(TaskState.Cancelled, TaskTransitionDecisionCode.TransitionNotAllowed)]
    [InlineData(TaskState.Completed, TaskTransitionDecisionCode.TransitionNotAllowed)]
    [InlineData(TaskState.Executing, TaskTransitionDecisionCode.TransitionNotAllowed)]
    public void Snooze_FromOtherState_ReturnsTransitionRejectedWithDecisionCode(
        TaskState state,
        TaskTransitionDecisionCode decisionCode)
    {
        var current = BaseInstance() with { State = state };
        var service = CreateService();

        var result = service.Snooze(current, TimeSpan.FromMinutes(5), Now);

        Assert.Equal(TaskCommandStatus.TransitionRejected, result.Status);
        Assert.Equal(decisionCode, result.TransitionDecisionCode);
        Assert.Null(result.Instance);
    }

    [Fact]
    public void Snooze_WhenExecuted_ReturnsAlreadyExecuted()
    {
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(generator: generator);

        var result = service.Snooze(
            ScheduledInstance() with { HasExecuted = true },
            TimeSpan.FromMinutes(5),
            Now);

        Assert.Equal(TaskCommandStatus.AlreadyExecuted, result.Status);
        Assert.Null(result.Instance);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void Cancel_FromScheduledAndWarning_SucceedsAndRefreshesToken()
    {
        var generator = new SequentialIdentifierGenerator(StageToken3, StageToken1);
        var service = CreateService(generator: generator);

        var scheduled = service.Cancel(ScheduledInstance());
        var warning = service.Cancel(WarningInstance());

        Assert.True(scheduled.Succeeded);
        Assert.Equal(TaskState.Cancelled, scheduled.Instance!.State);
        Assert.Equal(StageToken3, scheduled.Instance.StageToken);
        Assert.NotEqual(StageToken2, scheduled.Instance.StageToken);
        Assert.Null(scheduled.Instance.WarningStartTime);

        Assert.True(warning.Succeeded);
        Assert.Equal(TaskState.Cancelled, warning.Instance!.State);
        Assert.Equal(StageToken1, warning.Instance.StageToken);
        Assert.NotEqual(StageToken2, warning.Instance.StageToken);
    }

    [Fact]
    public void Cancel_FromOtherState_ReturnsTransitionRejected()
    {
        var service = CreateService();

        var result = service.Cancel(BaseInstance() with { State = TaskState.Idle });

        Assert.Equal(TaskCommandStatus.TransitionRejected, result.Status);
        Assert.Equal(TaskTransitionDecisionCode.TransitionNotAllowed, result.TransitionDecisionCode);
        Assert.Null(result.Instance);
    }

    [Fact]
    public void Cancel_WhenExecuted_ReturnsAlreadyExecuted()
    {
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(generator: generator);

        var result = service.Cancel(BaseInstance() with { HasExecuted = true });

        Assert.Equal(TaskCommandStatus.AlreadyExecuted, result.Status);
        Assert.Null(result.Instance);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void RescheduleDaily_WhenValid_KeepsIdentityAndRefreshesToken()
    {
        var definition = DailyAtDefinition();
        var current = BaseInstance() with
        {
            State = TaskState.Idle,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action
        };
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(generator: generator);

        var result = service.RescheduleDaily(
            definition,
            current,
            new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        var instance = result.Instance!;
        Assert.Equal(TaskState.Scheduled, instance.State);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 15, 0, 0, TimeSpan.Zero), instance.ScheduledFireTime);
        Assert.Equal(StageToken3, instance.StageToken);
        Assert.NotEqual(current.StageToken, instance.StageToken);
        Assert.Equal(current.InstanceId, instance.InstanceId);
        Assert.Equal(current.CreatedAt, instance.CreatedAt);
        Assert.False(instance.HasExecuted);
    }

    [Fact]
    public void RescheduleDaily_InvalidScenarios_AreRejected()
    {
        var service = CreateService();
        var definition = DailyAtDefinition();
        var idle = BaseInstance() with
        {
            State = TaskState.Idle,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action
        };

        var wrongKind = service.RescheduleDaily(CountdownDefinition(), idle, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, wrongKind.Status);

        var wrongId = service.RescheduleDaily(definition with { Id = AnotherTaskId }, idle, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, wrongId.Status);

        var wrongAction = service.RescheduleDaily(definition with { Action = PowerAction.Restart }, idle, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, wrongAction.Status);

        var notIdle = service.RescheduleDaily(definition, idle with { State = TaskState.Scheduled }, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.TransitionRejected, notIdle.Status);
        Assert.NotNull(notIdle.TransitionDecisionCode);

        var executed = service.RescheduleDaily(definition, idle with { HasExecuted = true }, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.AlreadyExecuted, executed.Status);
    }

    [Fact]
    public void RescheduleDaily_WhenScheduleFails_ReturnsScheduleCalculationFailed()
    {
        var definition = DailyAtDefinition();
        var idle = BaseInstance() with
        {
            State = TaskState.Idle,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action
        };
        var calculator = new FixedResultCalculator(
            new NextExecutionResult { Status = NextExecutionStatus.NoFutureOccurrence, Message = "failed" });
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(calculator: calculator, generator: generator);

        var result = service.RescheduleDaily(definition, idle, Now, TimeZoneInfo.Utc);

        Assert.Equal(TaskCommandStatus.ScheduleCalculationFailed, result.Status);
        Assert.Equal(NextExecutionStatus.NoFutureOccurrence, result.ScheduleStatus);
        Assert.Null(result.Instance);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void Create_WhenGeneratorReturnsEmptyOrDuplicateGuids_ReturnsInvalidCurrentInstance()
    {
        var emptyGenerator = new SequentialIdentifierGenerator(Guid.Empty, StageToken1);
        var emptyResult = CreateService(generator: emptyGenerator)
            .Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidCurrentInstance, emptyResult.Status);
        Assert.Null(emptyResult.Instance);

        var duplicateGenerator = new SequentialIdentifierGenerator(InstanceId1, InstanceId1);
        var duplicateResult = CreateService(generator: duplicateGenerator)
            .Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidCurrentInstance, duplicateResult.Status);
        Assert.Null(duplicateResult.Instance);
    }

    [Fact]
    public void Snooze_WhenGeneratorConflictsWithExistingIds_ReturnsInvalidCurrentInstance()
    {
        var current = ScheduledInstance();

        var sameAsOldToken = CreateService(generator: new SequentialIdentifierGenerator(current.StageToken))
            .Snooze(current, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(TaskCommandStatus.InvalidCurrentInstance, sameAsOldToken.Status);

        var sameAsInstanceId = CreateService(generator: new SequentialIdentifierGenerator(current.InstanceId))
            .Snooze(current, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(TaskCommandStatus.InvalidCurrentInstance, sameAsInstanceId.Status);

        var empty = CreateService(generator: new SequentialIdentifierGenerator(Guid.Empty))
            .Snooze(current, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(TaskCommandStatus.InvalidCurrentInstance, empty.Status);
    }

    [Fact]
    public void Cancel_WhenGeneratorConflicts_ReturnsInvalidCurrentInstance()
    {
        var current = ScheduledInstance();

        var result = CreateService(generator: new SequentialIdentifierGenerator(current.InstanceId))
            .Cancel(current);

        Assert.Equal(TaskCommandStatus.InvalidCurrentInstance, result.Status);
        Assert.Null(result.Instance);
    }

    [Fact]
    public void Commands_DoNotMutateTheInputInstance()
    {
        var service = CreateService();
        var original = ScheduledInstance();

        service.Snooze(original, TimeSpan.FromMinutes(5), Now);
        service.Cancel(original);
        service.RescheduleDaily(
            DailyAtDefinition(),
            original with { State = TaskState.Idle, SourceTaskId = SourceTaskId, ActionSnapshot = PowerAction.Shutdown },
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(TaskState.Scheduled, original.State);
        Assert.Equal(StageToken2, original.StageToken);
        Assert.False(original.HasExecuted);
    }

    [Fact]
    public void Results_ExposeZeroOffsetTimesAndFailuresWithNullInstanceAndMessage()
    {
        var service = CreateService();

        var created = service.Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.Zero, created.Instance!.ScheduledFireTime.Offset);
        Assert.Equal(TimeSpan.Zero, created.Instance.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, created.Instance.WarningStartTime!.Value.Offset);

        var invalid = service.Snooze(ScheduledInstance(), TimeSpan.Zero, Now);
        Assert.False(invalid.Succeeded);
        Assert.Null(invalid.Instance);
        Assert.False(string.IsNullOrEmpty(invalid.Message));
    }

    [Fact]
    public void SameInputWithSameGuidSequence_ProducesIdenticalResults()
    {
        var definition = CountdownDefinition();
        var first = CreateService(generator: new SequentialIdentifierGenerator(InstanceId1, StageToken1))
            .Create(definition, Now, TimeZoneInfo.Utc);
        var second = CreateService(generator: new SequentialIdentifierGenerator(InstanceId1, StageToken1))
            .Create(definition, Now, TimeZoneInfo.Utc);
        Assert.Equal(first, second);

        var current = ScheduledInstance();
        var snoozeFirst = CreateService(generator: new SequentialIdentifierGenerator(StageToken3))
            .Snooze(current, TimeSpan.FromMinutes(5), Now);
        var snoozeSecond = CreateService(generator: new SequentialIdentifierGenerator(StageToken3))
            .Snooze(current, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(snoozeFirst, snoozeSecond);
    }

    private static TaskService CreateService(
        INextExecutionCalculator? calculator = null,
        ITaskStateMachine? machine = null,
        IIdentifierGenerator? generator = null)
    {
        return new TaskService(
            calculator ?? new NextExecutionCalculator(),
            machine ?? new TaskStateMachine(),
            generator ?? new SequentialIdentifierGenerator(InstanceId1, StageToken1, StageToken3));
    }

    private static TaskDefinition CountdownDefinition(
        PowerAction action = PowerAction.Shutdown,
        int? warningSeconds = 60,
        TimeSpan? duration = null) => new()
        {
            Id = SourceTaskId,
            Kind = TaskKind.Countdown,
            Action = action,
            CountdownDuration = duration ?? TimeSpan.FromHours(2),
            WarningSeconds = warningSeconds,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };

    private static TaskDefinition DailyAtDefinition(PowerAction action = PowerAction.Shutdown) => new()
    {
        Id = SourceTaskId,
        Kind = TaskKind.DailyAt,
        Action = action,
        TargetTimeOfDay = new TimeOnly(15, 0),
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static TaskInstance BaseInstance() => new()
    {
        InstanceId = InstanceId1,
        SourceTaskId = SourceTaskId,
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskState.Scheduled,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = StageToken2,
        HasExecuted = false,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static TaskInstance ScheduledInstance() => BaseInstance() with { State = TaskState.Scheduled };

    private static TaskInstance WarningInstance() => BaseInstance() with { State = TaskState.Warning };

    private sealed class SequentialIdentifierGenerator : IIdentifierGenerator
    {
        private readonly Queue<Guid> _values;

        public SequentialIdentifierGenerator(params Guid[] values) => _values = new Queue<Guid>(values);

        public int CallCount { get; private set; }

        public Guid NewId()
        {
            CallCount++;
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("No more identifiers configured.");
            }

            return _values.Dequeue();
        }
    }

    private sealed class FixedResultCalculator : INextExecutionCalculator
    {
        private readonly NextExecutionResult _result;

        public FixedResultCalculator(NextExecutionResult result) => _result = result;

        public NextExecutionResult Calculate(
            TaskDefinition definition,
            DateTimeOffset now,
            TimeZoneInfo timeZone) => _result;
    }

    private sealed class RejectingStateMachine : ITaskStateMachine
    {
        public TaskTransitionResult TryTransition(
            TaskState current,
            TaskState target,
            TaskTransitionCause cause) => new()
            {
                Allowed = false,
                CurrentState = current,
                TargetState = target,
                Cause = cause,
                DecisionCode = TaskTransitionDecisionCode.TransitionNotAllowed,
                Message = "Simulated rejection."
            };
    }

    private sealed class RecordingStateMachine : ITaskStateMachine
    {
        private readonly TaskStateMachine _inner = new();

        public List<(TaskState Current, TaskState Target, TaskTransitionCause Cause)> Calls { get; } = [];

        public TaskTransitionResult TryTransition(
            TaskState current,
            TaskState target,
            TaskTransitionCause cause)
        {
            Calls.Add((current, target, cause));
            return _inner.TryTransition(current, target, cause);
        }
    }
}
