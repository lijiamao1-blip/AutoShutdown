using System.Diagnostics;
using System.Text.Json;
using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class SchedulerEngineTests
{
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid StageToken2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid StageToken3 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly DateTimeOffset ClockBase = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    [Fact(Timeout = 2000)]
    public async Task Run_WhenRuntimeFileIsMissing_StartsRunningWithNoTask()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running);

        var snapshot = engine.GetSnapshot();
        Assert.Equal(SchedulerEngineStatus.Running, snapshot.EngineStatus);
        Assert.Null(Current(snapshot));
        Assert.Equal(0, storage.WriteCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task Run_WhenRuntimeFileIsCorrupt_FaultsAndRejectsCommands()
    {
        var storage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var clock = new FakeClock(ClockBase);
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, new ControllableDeadline(), handler);

        using var scope = new EngineScope(engine);
        await scope.RunTask;

        Assert.Equal(SchedulerEngineStatus.Faulted, engine.GetSnapshot().EngineStatus);
        Assert.NotNull(engine.GetSnapshot().FaultMessage);

        var submit = engine.SubmitAsync(new CreateTaskCommand(CountdownDefinition()), CancellationToken.None);
        Assert.True(submit.IsCompleted);
        Assert.Equal(SchedulerCommandStatus.Faulted, (await submit).Status);
    }

    [Fact(Timeout = 2000)]
    public async Task Run_WhenRuntimeStateIsInvalid_Faults()
    {
        await AssertInvalidRuntimeStateFaultsAsync("null");
        await AssertInvalidRuntimeStateFaultsAsync(
            """{"SchemaVersion":2,"Instances":null,"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""");
        await AssertInvalidRuntimeStateFaultsAsync(
            """{"SchemaVersion":2,"Instances":{"99999999-9999-9999-9999-999999999999":{"InstanceId":"00000000-0000-0000-0000-000000000000","SourceTaskId":"99999999-9999-9999-9999-999999999999","ActionSnapshot":1,"State":1,"ScheduledFireTime":"2024-01-15T12:00:00+00:00","WarningStartTime":null,"StageToken":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","HasExecuted":false,"CreatedAt":"2024-01-15T10:00:00+00:00"}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""");
    }

    [Fact(Timeout = 2000)]
    public async Task Run_WhenRestoredScheduledIsInFuture_EstablishesWait()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", FutureScheduledRuntimeJson);
        var deadline = new ControllableDeadline();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), deadline, new FakeHandler());

        using var scope = new EngineScope(engine);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        Assert.Equal(SchedulerEngineStatus.Running, engine.GetSnapshot().EngineStatus);
        Assert.Equal(TaskInstanceState.Waiting, Current(engine.GetSnapshot())!.State);
    }

    [Fact(Timeout = 2000)]
    public async Task Create_Succeeds_RejectsWhenActive_AndFaultsOnPersistenceFailure()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using (var scope = new EngineScope(engine))
        {
            var created = await engine.SubmitAsync(
                new CreateTaskCommand(CountdownDefinition()),
                CancellationToken.None);

            Assert.True(created.Succeeded);
            Assert.Equal(SchedulerCommandStatus.Success, created.Status);
            Assert.Equal(1, storage.WriteCount);
            var current = Current(engine.GetSnapshot())!;
            Assert.NotNull(current);
            Assert.Equal(TaskInstanceState.Waiting, current.State);
            Assert.Equal(new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero), current.ScheduledFireTime);

            var second = await engine.SubmitAsync(
                new CreateTaskCommand(CountdownDefinition()),
                CancellationToken.None);

            Assert.Equal(SchedulerCommandStatus.ActiveTaskExists, second.Status);
        }

        var failingStorage = new InMemoryStorage { FailWriteAfter = 1 };
        var failingEngine = CreateEngine(failingStorage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using (var scope = new EngineScope(failingEngine))
        {
            var result = await failingEngine.SubmitAsync(
                new CreateTaskCommand(CountdownDefinition()),
                CancellationToken.None);

            Assert.Equal(SchedulerCommandStatus.PersistenceFailed, result.Status);
            Assert.Null(Current(failingEngine.GetSnapshot()));
            Assert.Equal(SchedulerEngineStatus.Faulted, failingEngine.GetSnapshot().EngineStatus);
        }
    }

    [Fact(Timeout = 2000)]
    public async Task SnoozeAndCancel_WithWrongIdentityOrToken_ReturnStaleWithoutWrites()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;
        Assert.Equal(1, storage.WriteCount);

        var wrongId = await engine.SubmitAsync(
            new SnoozeTaskCommand(Guid.NewGuid(), instance.StageToken, TimeSpan.FromMinutes(5)),
            CancellationToken.None);
        Assert.Equal(SchedulerCommandStatus.NoCurrentTask, wrongId.Status);

        var wrongToken = await engine.SubmitAsync(
            new SnoozeTaskCommand(instance.InstanceId, Guid.NewGuid(), TimeSpan.FromMinutes(5)),
            CancellationToken.None);
        Assert.Equal(SchedulerCommandStatus.StaleCommand, wrongToken.Status);

        var cancelWrongToken = await engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.Equal(SchedulerCommandStatus.StaleCommand, cancelWrongToken.Status);

        Assert.Equal(1, storage.WriteCount);
    }

    [Fact(Timeout = 2000)]
    public async Task Snooze_OldDeadlineAfterTokenRefresh_DoesNotChangeState()
    {
        var storage = new InMemoryStorage();
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(warningSeconds: 60)),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        var snoozed = await engine.SubmitAsync(
            new SnoozeTaskCommand(instance.InstanceId, instance.StageToken, TimeSpan.FromMinutes(5)),
            CancellationToken.None);
        Assert.True(snoozed.Succeeded);

        deadline.CompleteNext();
        await Task.Delay(50);

        var snapshot = engine.GetSnapshot();
        var current = Current(snapshot)!;
        Assert.Equal(TaskInstanceState.Waiting, current.State);
        Assert.NotEqual(instance.StageToken, current.StageToken);
        Assert.Equal(ClockBase.AddMinutes(5), current.ScheduledFireTime);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task Cancel_OldDeadline_DoesNotTriggerHandler()
    {
        var storage = new InMemoryStorage();
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        var cancelled = await engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken),
            CancellationToken.None);
        Assert.True(cancelled.Succeeded);
        deadline.CompleteNext();
        await Task.Delay(50);

        Assert.Equal(TaskInstanceState.Cancelled, Current(engine.GetSnapshot())!.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task ScheduledToWarning_PersistsOnce()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(warningSeconds: 60)),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 11, 59, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        Assert.Equal(TaskInstanceState.Confirming, Current(engine.GetSnapshot())!.State);
        Assert.Equal(2, storage.WriteCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task ScheduledWithoutWarning_ToExecutingThenCompleted_HandlerInvokedOnce()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Executed);

        Assert.Equal(1, handler.CallCount);
        var current = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Executed, current.State);
        Assert.True(current.HasExecuted);
        Assert.Equal(3, storage.WriteCount);
        Assert.Equal(SchedulerEngineStatus.Running, engine.GetSnapshot().EngineStatus);
    }

    [Fact(Timeout = 2000)]
    public async Task WarningToExecuting_HandlerInvokedOnce()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition(warningSeconds: 60)),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 11, 59, 0, TimeSpan.Zero);
        deadline.CompleteNext();
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Confirming);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Executed);

        Assert.Equal(1, handler.CallCount);
        var current = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Executed, current.State);
        Assert.True(current.HasExecuted);
        Assert.Equal(4, storage.WriteCount);
        Assert.Equal(SchedulerEngineStatus.Running, engine.GetSnapshot().EngineStatus);
    }

    [Fact(Timeout = 2000)]
    public async Task RepeatedDeadlineRelease_DoesNotDoubleInvokeHandler()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => handler.CallCount == 1);

        deadline.CompleteNext();
        deadline.CompleteAll();
        await Task.Delay(50);

        Assert.Equal(1, handler.CallCount);

        var instance = Current(engine.GetSnapshot())!;
        var rejected = await engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken),
            CancellationToken.None);
        Assert.Equal(SchedulerCommandStatus.TaskServiceRejected, rejected.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task PersistenceFailureBeforeExecuting_NoHandler_NoCommit_Faulted()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        storage.FailWriteAfter = 2;
        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Faulted);

        Assert.Equal(0, handler.CallCount);
        var current = Current(engine.GetSnapshot())!;
        Assert.False(current.HasExecuted);
        Assert.Equal(TaskInstanceState.Waiting, current.State);
    }

    [Fact(Timeout = 2000)]
    public async Task HandlerException_FaultsEngine_WithoutRetry()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler(() => throw new InvalidOperationException("Simulated handler failure."));
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Faulted);

        Assert.Equal(1, handler.CallCount);
        Assert.True(Current(engine.GetSnapshot())!.HasExecuted);
        Assert.NotNull(engine.GetSnapshot().FaultMessage);
    }

    [Fact(Timeout = 2000)]
    public async Task CommandAndDeadlineCompletingTogether_CommandWinsWithoutFiring()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var handler = new FakeHandler();
        var engine = CreateEngine(storage, clock, deadline, handler);

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var cancelTask = engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken),
            CancellationToken.None);
        deadline.CompleteNext();

        var cancelled = await cancelTask;

        Assert.True(cancelled.Succeeded);
        Assert.Equal(TaskInstanceState.Cancelled, Current(engine.GetSnapshot())!.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task ClearTerminal_ValidClearsInstance()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;

        var cancelled = await engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken),
            CancellationToken.None);
        Assert.True(cancelled.Succeeded);

        var cleared = await engine.SubmitAsync(
            new ClearTerminalTaskCommand(instance.InstanceId),
            CancellationToken.None);

        Assert.True(cleared.Succeeded);
        Assert.Empty(engine.GetSnapshot().Instances);
    }

    [Fact(Timeout = 2000)]
    public async Task ClearTerminal_InvalidStates_AreRejected()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;

        var onActive = await engine.SubmitAsync(
            new ClearTerminalTaskCommand(instance.InstanceId),
            CancellationToken.None);
        Assert.Equal(SchedulerCommandStatus.TransitionRejected, onActive.Status);

        var wrongId = await engine.SubmitAsync(
            new ClearTerminalTaskCommand(Guid.NewGuid()),
            CancellationToken.None);
        Assert.Equal(SchedulerCommandStatus.NoCurrentTask, wrongId.Status);
    }

    [Fact(Timeout = 2000)]
    public async Task ClearTerminal_PersistenceFailure_KeepsStateAndFaults()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;

        var cancelled = await engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken),
            CancellationToken.None);
        Assert.True(cancelled.Succeeded);

        storage.FailWriteAfter = 3;
        var cleared = await engine.SubmitAsync(
            new ClearTerminalTaskCommand(instance.InstanceId),
            CancellationToken.None);

        Assert.Equal(SchedulerCommandStatus.PersistenceFailed, cleared.Status);
        Assert.Equal(TaskInstanceState.Cancelled, Current(engine.GetSnapshot())!.State);
        Assert.Equal(SchedulerEngineStatus.Faulted, engine.GetSnapshot().EngineStatus);
    }

    [Fact(Timeout = 2000)]
    public async Task ConcurrentCreateCommands_AreProcessedSerially_OnlyOneSucceeds()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using var scope = new EngineScope(engine);
        var first = engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        var second = engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result.Status == SchedulerCommandStatus.Success));
        Assert.Equal(1, results.Count(result => result.Status == SchedulerCommandStatus.ActiveTaskExists));
        Assert.Equal(1, storage.WriteCount);
    }

    [Fact(Timeout = 2000)]
    public async Task Submit_WhenNotRunningFaultedOrStopped_ReturnsQuickly()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        var notRunning = engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(notRunning.IsCompleted);
        Assert.Equal(SchedulerCommandStatus.NotRunning, (await notRunning).Status);

        var faultedStorage = new InMemoryStorage { ReadStatus = StorageReadStatus.Corrupt };
        var faultedEngine = CreateEngine(faultedStorage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());
        using (var faultedScope = new EngineScope(faultedEngine))
        {
            await faultedScope.RunTask;

            var faulted = faultedEngine.SubmitAsync(
                new CreateTaskCommand(CountdownDefinition()),
                CancellationToken.None);
            Assert.True(faulted.IsCompleted);
            Assert.Equal(SchedulerCommandStatus.Faulted, (await faulted).Status);
        }

        using var cts = new CancellationTokenSource();
        var runTask = engine.RunAsync(cts.Token);
        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running);
        cts.Cancel();
        await runTask;

        Assert.Equal(SchedulerEngineStatus.Stopped, engine.GetSnapshot().EngineStatus);

        var stopped = engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(stopped.IsCompleted);
        Assert.Equal(SchedulerCommandStatus.NotRunning, (await stopped).Status);
    }

    [Fact(Timeout = 2000)]
    public async Task CancelRunAsync_CompletesPendingCommands_WithoutHanging()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using var cts = new CancellationTokenSource();
        var runTask = engine.RunAsync(cts.Token);
        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running);

        var pending = engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);

        cts.Cancel();
        await runTask;
        await WaitUntilAsync(() => pending.IsCompleted);

        Assert.Contains(
            (await pending).Status,
            new[] { SchedulerCommandStatus.Success, SchedulerCommandStatus.NotRunning });
        Assert.Equal(SchedulerEngineStatus.Stopped, engine.GetSnapshot().EngineStatus);
    }

    [Fact]
    public void Snapshot_IsStableAndImmutable()
    {
        var storage = new InMemoryStorage();
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        var first = engine.GetSnapshot();
        var second = engine.GetSnapshot();

        Assert.Equal(first, second);
        Assert.Equal(SchedulerEngineStatus.Created, first.EngineStatus);
    }

    // ---- S8 集成：ShutdownScheduledTaskHandler + ShutdownWorkflow + 电源唯一出口 ----

    [Fact(Timeout = 2000)]
    public async Task DueTask_PowerOnceThenCompleted_EngineRunning()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new FakePowerService();
        var engine = CreateWorkflowEngine(
            storage,
            clock,
            deadline,
            power,
            new FixedConfigurationService(SuccessConfig()));

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Executed);

        Assert.Equal(SchedulerEngineStatus.Running, engine.GetSnapshot().EngineStatus);
        var completed = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Executed, completed.State);
        Assert.True(completed.HasExecuted);
        Assert.Single(power.Invocations);
        Assert.Equal(3, storage.WriteCount);
    }

    [Fact(Timeout = 2000)]
    public async Task DueTask_WhenWorkflowRejects_ThenFailed_EngineRunning()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new FakePowerService();
        var engine = CreateWorkflowEngine(
            storage,
            clock,
            deadline,
            power,
            new FixedConfigurationService(
            new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Missing, Errors = ["missing"] }));

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Faulted);

        Assert.Equal(SchedulerEngineStatus.Running, engine.GetSnapshot().EngineStatus);
        Assert.Equal(TaskInstanceState.Faulted, Current(engine.GetSnapshot())!.State);
        Assert.Empty(power.Invocations);
        Assert.Equal(3, storage.WriteCount);
    }

    [Fact(Timeout = 2000)]
    public async Task DueTask_WhenPowerFails_ThenFailed_NoSecondCall()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new RecordingPowerService(new PowerResult
        {
            Outcome = PowerOutcome.Failed,
            WasSimulated = false,
            Message = "power failed"
        });
        var engine = CreateWorkflowEngine(
            storage,
            clock,
            deadline,
            power,
            new FixedConfigurationService(SuccessConfig()));

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Faulted);

        Assert.Equal(TaskInstanceState.Faulted, Current(engine.GetSnapshot())!.State);
        Assert.Single(power.Requests);
    }

    [Fact(Timeout = 2000)]
    public async Task CompletedPersistenceFailure_FaultsKeepingExecuting_NoSecondCall()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new FakePowerService();
        var engine = CreateWorkflowEngine(
            storage,
            clock,
            deadline,
            power,
            new FixedConfigurationService(SuccessConfig()));

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        storage.FailWriteAfter = 3;
        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Faulted);

        Assert.Single(power.Invocations);
        var current = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Executing, current.State);
        Assert.True(current.HasExecuted);
    }

    [Fact(Timeout = 2000)]
    public async Task FailedPersistenceFailure_FaultsKeepingExecuting()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new FakePowerService();
        var engine = CreateWorkflowEngine(
            storage,
            clock,
            deadline,
            power,
            new FixedConfigurationService(
            new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Missing, Errors = ["missing"] }));

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        storage.FailWriteAfter = 3;
        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();

        await WaitUntilAsync(() => engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Faulted);

        Assert.Empty(power.Invocations);
        var current = Current(engine.GetSnapshot())!;
        Assert.Equal(TaskInstanceState.Executing, current.State);
        Assert.True(current.HasExecuted);
    }

    [Fact(Timeout = 2000)]
    public async Task StaleCallbacksAndCommands_DoNotCauseSecondPowerCall()
    {
        var storage = new InMemoryStorage();
        var clock = new FakeClock(ClockBase);
        var deadline = new ControllableDeadline();
        var power = new FakePowerService();
        var engine = CreateWorkflowEngine(
            storage,
            clock,
            deadline,
            power,
            new FixedConfigurationService(SuccessConfig()));

        using var scope = new EngineScope(engine);
        var created = await engine.SubmitAsync(
            new CreateTaskCommand(CountdownDefinition()),
            CancellationToken.None);
        Assert.True(created.Succeeded);
        var instance = Current(engine.GetSnapshot())!;
        await WaitUntilAsync(() => deadline.PendingCount >= 1);

        clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        deadline.CompleteNext();
        await WaitUntilAsync(() => Current(engine.GetSnapshot())?.State == TaskInstanceState.Executed);

        Assert.Single(power.Invocations);

        deadline.CompleteNext();
        deadline.CompleteAll();
        await Task.Delay(50);

        var stale = await engine.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.Equal(SchedulerCommandStatus.StaleCommand, stale.Status);

        Assert.Single(power.Invocations);
    }

    [Fact]
    public void AppCompositionRoot_RegistersGuardedPowerAndShutdownHandler_WithoutRealPowerApis()
    {
        var registration = FindSourceFile("ServiceRegistration.cs");
        var source = File.ReadAllText(registration);

        // The composition root registers the guarded power router (fake + win32),
        // keeps the real shutdown handler; the workflow is registered through a
        // factory and wrapped by the logging decorator only.
        Assert.Contains("AddSingleton<FakePowerService>", source);
        Assert.Contains("new GuardedPowerService(", source);
        Assert.DoesNotContain("AddSingleton<IPowerService, FakePowerService>", source);
        Assert.Contains("AddSingleton<ShutdownWorkflow>(provider =>", source);
        Assert.Contains("AddSingleton<IPrePipelineRunner>", source);
        Assert.Contains("GetRequiredService<IPrePipelineRunner>()", source);
        Assert.Contains("AddSingleton<IShutdownWorkflow>", source);
        Assert.Contains("LoggingShutdownWorkflowDecorator", source);
        Assert.Contains("GetRequiredService<ShutdownWorkflow>()", source);
        Assert.Contains("GetRequiredService<IApplicationLogger>()", source);
        // S21：handler 经工厂注册并注入 WoL 执行器（WoL 任务经调度派发，不经真实电源）。
        Assert.Contains("AddSingleton<IScheduledTaskHandler>(provider =>", source);
        Assert.Contains("new ShutdownScheduledTaskHandler(", source);
        Assert.Contains("GetRequiredService<IWakeOnLanTaskExecutor>()", source);

        var srcDirectory = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(registration)!, "..", ".."));
        var sources = Directory.GetFiles(srcDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        foreach (var file in sources)
        {
            var name = Path.GetFileName(file);
            var content = File.ReadAllText(file);

            if (name == "Win32PowerNativeApi.cs")
            {
                Assert.Contains("DllImport", content);
            }
            else
            {
                Assert.DoesNotContain("DllImport", content);
                Assert.DoesNotContain("ExitWindowsEx", content);
                Assert.DoesNotContain("SetSuspendState", content);
            }

            Assert.DoesNotContain("shutdown.exe", content);
            if (name == "OfficeSaveHelperLauncher.cs")
            {
                Assert.Contains("Process.Start", content);
            }
            else
            {
                Assert.DoesNotContain("Process.Start", content);
            }
        }

        // Runtime wiring: guarded power, logging-decorated workflow, real handler.
        var services = new ServiceCollection();
        services.AddAutoShutdownServices();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<GuardedPowerService>(provider.GetRequiredService<IPowerService>());
        Assert.IsType<LoggingShutdownWorkflowDecorator>(provider.GetRequiredService<IShutdownWorkflow>());
        Assert.IsType<ShutdownScheduledTaskHandler>(provider.GetRequiredService<IScheduledTaskHandler>());
    }

    private static string FindSourceFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "AutoShutdown.App", "AppHost", "ServiceRegistration.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{fileName} was not found.");
    }

    private static TaskInstance? Current(SchedulerSnapshot snapshot)
        => snapshot.Instances.Values.FirstOrDefault();

    private static async Task AssertInvalidRuntimeStateFaultsAsync(string json)
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", json);
        var engine = CreateEngine(storage, new FakeClock(ClockBase), new ControllableDeadline(), new FakeHandler());

        using var scope = new EngineScope(engine);
        await scope.RunTask;

        Assert.Equal(SchedulerEngineStatus.Faulted, engine.GetSnapshot().EngineStatus);
    }

    private static SchedulerEngine CreateEngine(
        IStorage storage,
        FakeClock clock,
        ControllableDeadline deadline,
        FakeHandler handler)
    {
        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(InstanceId1, StageToken1, StageToken2, StageToken3, StageToken2));

        return new SchedulerEngine(
            storage,
            clock,
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(StageToken1, StageToken2, StageToken3),
            handler,
            new NoOpTaskArbitrator());
    }

    private static SchedulerEngine CreateWorkflowEngine(
        IStorage storage,
        FakeClock clock,
        ControllableDeadline deadline,
        IPowerService powerService,
        IConfigurationService configurationService)
    {
        var workflow = new ShutdownWorkflow(configurationService, powerService);
        var handler = new ShutdownScheduledTaskHandler(workflow);

        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(InstanceId1, StageToken1, StageToken2, StageToken3, StageToken2));

        return new SchedulerEngine(
            storage,
            clock,
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            new SequentialIdentifierGenerator(StageToken1, StageToken2, StageToken3),
            handler,
            new NoOpTaskArbitrator());
    }

    private static ConfigurationLoadResult SuccessConfig() => new()
    {
        Status = ConfigurationLoadStatus.Success,
        Config = new AppConfig
        {
            SchemaVersion = 1,
            TestMode = true,
            AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
            Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
        }
    };

    private static TaskDefinition CountdownDefinition(
        int? warningSeconds = null,
        TimeSpan? duration = null) => new()
        {
            Id = SourceTaskId,
            Kind = TaskKind.Countdown,
            Action = PowerAction.Shutdown,
            CountdownDuration = duration ?? TimeSpan.FromHours(2),
            WarningSeconds = warningSeconds,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for the condition.");
            }

            await Task.Delay(5);
        }
    }

    private const string FutureScheduledRuntimeJson =
        """{"SchemaVersion":2,"Instances":{"99999999-9999-9999-9999-999999999999":{"InstanceId":"11111111-1111-1111-1111-111111111111","SourceTaskId":"99999999-9999-9999-9999-999999999999","ActionSnapshot":1,"State":1,"ScheduledFireTime":"2024-01-15T12:00:00+00:00","WarningStartTime":null,"StageToken":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","HasExecuted":false,"CreatedAt":"2024-01-15T10:00:00+00:00"}},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class ControllableDeadline : IAsyncDeadline
    {
        private readonly List<TaskCompletionSource> _waiters = new();
        private readonly object _gate = new();

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        public Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiters.Add(tcs);
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => tcs.TrySetCanceled());
            }

            return tcs.Task;
        }

        public void CompleteNext()
        {
            TaskCompletionSource? next = null;
            lock (_gate)
            {
                while (_waiters.Count > 0)
                {
                    next = _waiters[0];
                    _waiters.RemoveAt(0);
                    if (!next.Task.IsCompleted)
                    {
                        break;
                    }

                    next = null;
                }
            }

            next?.TrySetResult();
        }

        public void CompleteAll()
        {
            List<TaskCompletionSource> waiters;
            lock (_gate)
            {
                waiters = new List<TaskCompletionSource>(_waiters);
                _waiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                waiter.TrySetResult();
            }
        }
    }

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, string> _documents = new();
        private int _writeCount;

        public int WriteCount => _writeCount;

        public int FailWriteAfter { get; set; } = -1;

        public StorageReadStatus ReadStatus { get; init; } = StorageReadStatus.Success;

        public void Seed(string relativePath, string json) => _documents[relativePath] = json;

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (ReadStatus != StorageReadStatus.Success)
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = ReadStatus,
                    Error = "Simulated read failure."
                });
            }

            if (!_documents.TryGetValue(relativePath, out var json))
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.NotFound
                });
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(json);
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Success,
                    Value = value
                });
            }
            catch (JsonException)
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Corrupt,
                    Error = "Corrupt JSON."
                });
            }
        }

        public Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
        {
            _writeCount++;
            if (FailWriteAfter > 0 && _writeCount >= FailWriteAfter)
            {
                return Task.FromResult(new StorageWriteResult
                {
                    Status = StorageWriteStatus.IoFailure,
                    Error = "Simulated write failure."
                });
            }

            _documents[relativePath] = JsonSerializer.Serialize(value);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
        }
    }

    private sealed class FakeHandler : IScheduledTaskHandler
    {
        private readonly Action? _onDue;

        public FakeHandler(Action? onDue = null) => _onDue = onDue;

        public List<TaskInstance> Calls { get; } = new();

        public int CallCount => Calls.Count;

        public Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            Calls.Add(instance);
            _onDue?.Invoke();
            return Task.CompletedTask;
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
        private readonly List<PowerRequest> _requests = new();
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

    private sealed class EngineScope : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public EngineScope(SchedulerEngine engine)
        {
            Engine = engine;
            RunTask = engine.RunAsync(_cts.Token);
        }

        public SchedulerEngine Engine { get; }

        public Task RunTask { get; }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                RunTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
