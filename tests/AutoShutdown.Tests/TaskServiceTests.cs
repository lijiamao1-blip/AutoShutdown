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
        Assert.Equal(TaskInstanceState.Waiting, instance.State);
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
    public void Create_DoesNotInvokeStateMachine_InstanceIsBornWaiting()
    {
        var machine = new RecordingStateMachine();
        var generator = new SequentialIdentifierGenerator(InstanceId1, StageToken1);
        var service = CreateService(machine: machine, generator: generator);

        var result = service.Create(CountdownDefinition(), Now, TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(TaskInstanceState.Waiting, result.Instance!.State);
        Assert.Empty(machine.Calls); // V2：实例「诞生」于 Waiting，无需从 Idle 的状态机转换。
    }

    [Fact]
    public void Snooze_FromWaiting_ReschedulesWithoutStateTransition()
    {
        var current = ScheduledInstance();
        var machine = new RecordingStateMachine();
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(machine: machine, generator: generator);

        var result = service.Snooze(current, TimeSpan.FromMinutes(5), Now);

        Assert.True(result.Succeeded);
        Assert.Equal(TaskInstanceState.Waiting, result.Instance!.State);
        Assert.Equal(Now.AddMinutes(5).ToUniversalTime(), result.Instance.ScheduledFireTime);
        Assert.Null(result.Instance.WarningStartTime);
        Assert.Equal(StageToken3, result.Instance.StageToken);
        Assert.Empty(machine.Calls); // Waiting→Waiting 是字段级重排，不走状态机。
    }

    [Fact]
    public void Snooze_FromConfirming_ReturnsTransitionRejected()
    {
        var current = WarningInstance();
        var service = CreateService();

        var result = service.Snooze(current, TimeSpan.FromMinutes(10), Now);

        // V2 白名单无 confirming→waiting 边：确认态实例不可 snooze。
        Assert.Equal(TaskCommandStatus.TransitionRejected, result.Status);
        Assert.NotNull(result.TransitionDecisionCode);
        Assert.Null(result.Instance);
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
    [InlineData(TaskInstanceState.Confirming)]
    [InlineData(TaskInstanceState.Executing)]
    [InlineData(TaskInstanceState.Cancelled)]
    [InlineData(TaskInstanceState.Executed)]
    [InlineData(TaskInstanceState.Faulted)]
    [InlineData(TaskInstanceState.Interrupted)]
    public void Snooze_FromNonWaitingState_ReturnsTransitionRejected(TaskInstanceState state)
    {
        var current = BaseInstance() with { State = state };
        var service = CreateService();

        var result = service.Snooze(current, TimeSpan.FromMinutes(5), Now);

        Assert.Equal(TaskCommandStatus.TransitionRejected, result.Status);
        Assert.NotNull(result.TransitionDecisionCode);
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
    public void Cancel_FromWaitingAndConfirming_SucceedsAndRefreshesToken()
    {
        var generator = new SequentialIdentifierGenerator(StageToken3, StageToken1);
        var service = CreateService(generator: generator);

        var scheduled = service.Cancel(ScheduledInstance());
        var warning = service.Cancel(WarningInstance());

        Assert.True(scheduled.Succeeded);
        Assert.Equal(TaskInstanceState.Cancelled, scheduled.Instance!.State);
        Assert.Equal(StageToken3, scheduled.Instance.StageToken);
        Assert.NotEqual(StageToken2, scheduled.Instance.StageToken);
        Assert.Null(scheduled.Instance.WarningStartTime);

        Assert.True(warning.Succeeded);
        Assert.Equal(TaskInstanceState.Cancelled, warning.Instance!.State);
        Assert.Equal(StageToken1, warning.Instance.StageToken);
        Assert.NotEqual(StageToken2, warning.Instance.StageToken);
    }

    [Fact]
    public void Cancel_FromOtherState_ReturnsTransitionRejected()
    {
        var service = CreateService();

        var result = service.Cancel(BaseInstance() with { State = TaskInstanceState.Executing });

        Assert.Equal(TaskCommandStatus.TransitionRejected, result.Status);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.TransitionNotAllowed, result.TransitionDecisionCode);
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
            State = TaskInstanceState.Executed,
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
        Assert.Equal(TaskInstanceState.Waiting, instance.State);
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
        var executed = BaseInstance() with
        {
            State = TaskInstanceState.Executed,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action
        };

        var wrongKind = service.RescheduleDaily(CountdownDefinition(), executed, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, wrongKind.Status);

        var wrongId = service.RescheduleDaily(definition with { Id = AnotherTaskId }, executed, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, wrongId.Status);

        var wrongAction = service.RescheduleDaily(definition with { Action = PowerAction.Restart }, executed, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, wrongAction.Status);

        // V2 仅 executed→waiting 可重排；Waiting 态不可重排。
        var notExecuted = service.RescheduleDaily(definition, executed with { State = TaskInstanceState.Waiting }, Now, TimeZoneInfo.Utc);
        Assert.Equal(TaskCommandStatus.TransitionRejected, notExecuted.Status);
        Assert.NotNull(notExecuted.TransitionDecisionCode);
    }

    [Fact]
    public void RescheduleDaily_WhenScheduleFails_ReturnsScheduleCalculationFailed()
    {
        var definition = DailyAtDefinition();
        var executed = BaseInstance() with
        {
            State = TaskInstanceState.Executed,
            SourceTaskId = definition.Id,
            ActionSnapshot = definition.Action
        };
        var calculator = new FixedResultCalculator(
            new NextExecutionResult { Status = NextExecutionStatus.NoFutureOccurrence, Message = "failed" });
        var generator = new SequentialIdentifierGenerator(StageToken3);
        var service = CreateService(calculator: calculator, generator: generator);

        var result = service.RescheduleDaily(definition, executed, Now, TimeZoneInfo.Utc);

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
            original with { State = TaskInstanceState.Executed, SourceTaskId = SourceTaskId, ActionSnapshot = PowerAction.Shutdown },
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(TaskInstanceState.Waiting, original.State);
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
        ITaskInstanceStateMachine? machine = null,
        IIdentifierGenerator? generator = null)
    {
        return new TaskService(
            calculator ?? new NextExecutionCalculator(),
            machine ?? new TaskInstanceStateMachine(),
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
        State = TaskInstanceState.Waiting,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
        WarningStartTime = null,
        StageToken = StageToken2,
        HasExecuted = false,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static TaskInstance ScheduledInstance() => BaseInstance() with { State = TaskInstanceState.Waiting };

    private static TaskInstance WarningInstance() => BaseInstance() with { State = TaskInstanceState.Confirming };

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

    private sealed class RecordingStateMachine : ITaskInstanceStateMachine
    {
        private readonly TaskInstanceStateMachine _inner = new();

        public List<(TaskInstanceState Current, TaskInstanceState Target, TaskInstanceStateTransitionCause Cause)> Calls { get; } = [];

        public TaskInstanceStateTransitionResult TryTransition(
            TaskInstanceState current,
            TaskInstanceState target,
            TaskInstanceStateTransitionCause cause,
            string? source = null)
        {
            Calls.Add((current, target, cause));
            return _inner.TryTransition(current, target, cause, source);
        }
    }
}
