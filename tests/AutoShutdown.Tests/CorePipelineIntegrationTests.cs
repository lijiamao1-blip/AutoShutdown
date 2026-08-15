using System.Diagnostics;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class CorePipelineIntegrationTests
{
    private static readonly Guid SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid InstanceId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StageToken1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid StageToken2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid StageToken3 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly DateTimeOffset ClockBase = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "Test Plus Nine Thirty",
        TimeSpan.FromHours(9.5),
        "Test Plus Nine Thirty",
        "Test Plus Nine Thirty");

    // ---- A. 合法执行恰好一次 ----

    [Fact(Timeout = 2000)]
    public async Task A_LegalCountdown_ExecutesExactlyOnce_ThenCompletes()
    {
        using var harness = new Harness();
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        Assert.Equal(TaskState.Scheduled, harness.Engine.GetSnapshot().CurrentInstance!.State);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Completed);

        Assert.Equal(SchedulerEngineStatus.Running, harness.Engine.GetSnapshot().EngineStatus);
        Assert.Equal(TaskState.Completed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(1, harness.Power.CallCount);
        Assert.Equal(PowerAction.Shutdown, harness.Power.Requests[0].Action);
        Assert.Equal(harness.CreatedInstanceId, harness.Power.Requests[0].InstanceId);
        Assert.False(string.IsNullOrEmpty(harness.Power.Requests[0].Reason));

        var states = harness.Storage.WrittenRuntimeStates;
        var executingIndex = states.FindIndex(s => s.CurrentInstance?.State == TaskState.Executing && s.CurrentInstance.HasExecuted);
        var completedIndex = states.FindIndex(s => s.CurrentInstance?.State == TaskState.Completed);
        Assert.True(executingIndex >= 0, "Executing must be persisted.");
        Assert.True(completedIndex > executingIndex, "Executing must be persisted before Completed.");

        harness.Deadline.CompleteNext();
        harness.Deadline.CompleteAll();
        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 13, 0, 0, TimeSpan.Zero);
        await Task.Delay(50);

        Assert.Equal(1, harness.Power.CallCount);
        Assert.Equal(3, harness.Storage.WriteCount);
    }

    // ---- B. 取消后零执行 ----

    [Fact(Timeout = 2000)]
    public async Task B_CancelBeforeFire_NeverExecutes()
    {
        using var harness = new Harness();
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        var instance = harness.Engine.GetSnapshot().CurrentInstance!;
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        var cancelled = await harness.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken));
        Assert.True(cancelled.Succeeded);
        Assert.Equal(TaskState.Cancelled, harness.Engine.GetSnapshot().CurrentInstance!.State);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();
        harness.Deadline.CompleteAll();
        await Task.Delay(50);

        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(TaskState.Cancelled, harness.Engine.GetSnapshot().CurrentInstance!.State);
        var states = harness.Storage.WrittenRuntimeStates;
        Assert.DoesNotContain(states, s => s.CurrentInstance?.State == TaskState.Executing);
        Assert.DoesNotContain(states, s => s.CurrentInstance?.State == TaskState.Completed);
    }

    // ---- C. 延迟后旧时间零执行，新时间一次 ----

    [Fact(Timeout = 2000)]
    public async Task C_SnoozeAfterWarning_OldDeadlinesZero_NewFireOnce()
    {
        using var harness = new Harness();
        var created = await harness.SubmitCreateAsync(CountdownDefinition(warningSeconds: 60));
        Assert.True(created.Succeeded);
        var instance = harness.Engine.GetSnapshot().CurrentInstance!;
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 11, 59, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();
        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Warning);

        var snoozed = await harness.SubmitAsync(
            new SnoozeTaskCommand(instance.InstanceId, instance.StageToken, TimeSpan.FromMinutes(30)));
        Assert.True(snoozed.Succeeded);
        var snoozedInstance = harness.Engine.GetSnapshot().CurrentInstance!;
        Assert.NotEqual(instance.StageToken, snoozedInstance.StageToken);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 12, 29, 0, TimeSpan.Zero), snoozedInstance.ScheduledFireTime);

        harness.Deadline.CompleteNext();
        harness.Deadline.CompleteAll();
        await Task.Delay(50);
        Assert.Equal(0, harness.Power.CallCount);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 29, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();
        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Completed);

        Assert.Equal(1, harness.Power.CallCount);
        Assert.Equal(TaskState.Completed, harness.Engine.GetSnapshot().CurrentInstance!.State);
    }

    // ---- D. 配置损坏或不可用零执行 ----

    [Theory(Timeout = 2000)]
    [InlineData(ConfigurationLoadStatus.Corrupt)]
    [InlineData(ConfigurationLoadStatus.Invalid)]
    [InlineData(ConfigurationLoadStatus.IoFailure)]
    [InlineData(ConfigurationLoadStatus.Missing)]
    public async Task D_UnavailableConfiguration_ExecutesNothing_ThenFailed(ConfigurationLoadStatus status)
    {
        using var harness = new Harness(
            configResult: new ConfigurationLoadResult { Status = status, Errors = ["unavailable"] });
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Failed);

        Assert.Equal(SchedulerEngineStatus.Running, harness.Engine.GetSnapshot().EngineStatus);
        Assert.Equal(TaskState.Failed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(1, harness.Configuration.LoadCount);
    }

    [Fact(Timeout = 2000)]
    public async Task D_TestModeDisabled_ExecutesNothing_ThenFailed()
    {
        using var harness = new Harness(config: ValidConfig() with { TestMode = false });
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Failed);

        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(TaskState.Failed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(SchedulerEngineStatus.Running, harness.Engine.GetSnapshot().EngineStatus);
    }

    [Fact(Timeout = 2000)]
    public async Task D_ActionNotAllowed_ExecutesNothing_ThenFailed()
    {
        var config = ValidConfig() with { AllowedActions = [PowerAction.Sleep] };
        using var harness = new Harness(config: config);
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Failed);

        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(TaskState.Failed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(SchedulerEngineStatus.Running, harness.Engine.GetSnapshot().EngineStatus);
    }

    // ---- E. runtime 损坏恢复零执行 ----

    [Theory(Timeout = 2000)]
    [InlineData(StorageReadStatus.Corrupt)]
    [InlineData(StorageReadStatus.IoFailure)]
    public async Task E_CorruptRuntimeRecovery_FaultsWithoutAnyCall(StorageReadStatus status)
    {
        var storage = new InMemoryStorage { ReadStatus = status };
        using var harness = new Harness(storage: storage, startRunning: true);
        await harness.RunTask;

        Assert.Equal(SchedulerEngineStatus.Faulted, harness.Engine.GetSnapshot().EngineStatus);
        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(0, harness.Configuration.LoadCount);

        var submit = harness.Engine.SubmitAsync(new CreateTaskCommand(CountdownDefinition()), CancellationToken.None);
        Assert.True(submit.IsCompleted);
        Assert.Contains(
            (await submit).Status,
            new[] { SchedulerCommandStatus.Faulted, SchedulerCommandStatus.NotRunning });
    }

    // ---- F. 崩溃恢复绝不补执行 ----

    [Fact(Timeout = 2000)]
    public async Task F_RecoverWarning_Interrupted_NoExecution()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", WarningRuntimeJson);
        using var harness = new Harness(storage: storage, startRunning: true);

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Interrupted);

        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(0, harness.Configuration.LoadCount);
        Assert.False(harness.Engine.GetSnapshot().CurrentInstance!.HasExecuted);
    }

    [Fact(Timeout = 2000)]
    public async Task F_RecoverExecuting_Interrupted_NoExecution()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", ExecutingRuntimeJson);
        using var harness = new Harness(storage: storage, startRunning: true);

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Interrupted);

        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(0, harness.Configuration.LoadCount);
        Assert.True(harness.Engine.GetSnapshot().CurrentInstance!.HasExecuted);
    }

    [Fact(Timeout = 2000)]
    public async Task F_RecoverMissedScheduled_Faults_PreservingInstance_NoExecution()
    {
        var storage = new InMemoryStorage();
        storage.Seed("runtime.json", MissedScheduledRuntimeJson);
        using var harness = new Harness(storage: storage, startRunning: true);
        await harness.RunTask;

        var snapshot = harness.Engine.GetSnapshot();
        Assert.Equal(SchedulerEngineStatus.Faulted, snapshot.EngineStatus);
        Assert.Equal(TaskState.Scheduled, snapshot.CurrentInstance!.State);
        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(0, harness.Configuration.LoadCount);
    }

    // ---- G. 重复与过期回调 ----

    [Fact(Timeout = 2000)]
    public async Task G_DuplicateDeadlineCompletion_PowerAtMostOnce()
    {
        using var harness = new Harness();
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();
        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Completed);
        Assert.Equal(1, harness.Power.CallCount);

        harness.Deadline.CompleteNext();
        harness.Deadline.CompleteAll();
        await Task.Delay(50);

        Assert.Equal(1, harness.Power.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task G_OldStageTokenCallback_AfterSnooze_PowerZero()
    {
        using var harness = new Harness();
        var created = await harness.SubmitCreateAsync(CountdownDefinition(warningSeconds: 60));
        Assert.True(created.Succeeded);
        var instance = harness.Engine.GetSnapshot().CurrentInstance!;
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 11, 59, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();
        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Warning);

        var snoozed = await harness.SubmitAsync(
            new SnoozeTaskCommand(instance.InstanceId, instance.StageToken, TimeSpan.FromMinutes(30)));
        Assert.True(snoozed.Succeeded);

        harness.Deadline.CompleteNext();
        harness.Deadline.CompleteAll();
        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await Task.Delay(50);

        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(TaskState.Scheduled, harness.Engine.GetSnapshot().CurrentInstance!.State);
    }

    [Fact(Timeout = 2000)]
    public async Task G_CommandAndDeadlineTogether_CommandWins_NoExecution()
    {
        using var harness = new Harness();
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        var instance = harness.Engine.GetSnapshot().CurrentInstance!;
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var cancelTask = harness.SubmitAsync(
            new CancelTaskCommand(instance.InstanceId, instance.StageToken));
        harness.Deadline.CompleteNext();

        var cancelled = await cancelTask;

        Assert.True(cancelled.Succeeded);
        Assert.Equal(TaskState.Cancelled, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(0, harness.Power.CallCount);
    }

    // ---- H. 持久化失败安全性 ----

    [Fact(Timeout = 2000)]
    public async Task H_ScheduledWriteFailure_CreateFails_Faulted_NoPower()
    {
        var storage = new InMemoryStorage { FailWriteAfter = 1 };
        using var harness = new Harness(storage: storage);

        var created = await harness.SubmitCreateAsync(CountdownDefinition());

        Assert.Equal(SchedulerCommandStatus.PersistenceFailed, created.Status);
        Assert.Equal(0, harness.Power.CallCount);
        Assert.Equal(SchedulerEngineStatus.Faulted, harness.Engine.GetSnapshot().EngineStatus);
        Assert.Null(harness.Engine.GetSnapshot().CurrentInstance);
    }

    [Fact(Timeout = 2000)]
    public async Task H_ExecutingWriteFailure_KeepsScheduled_NoPower_Faulted()
    {
        var storage = new InMemoryStorage();
        using var harness = new Harness(storage: storage);
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        storage.FailWriteAfter = 2;
        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Faulted);

        Assert.Equal(0, harness.Power.CallCount);
        var current = harness.Engine.GetSnapshot().CurrentInstance!;
        Assert.Equal(TaskState.Scheduled, current.State);
        Assert.False(current.HasExecuted);
    }

    [Fact(Timeout = 2000)]
    public async Task H_CompletedWriteFailure_KeepsExecuting_NoSecondCall_Faulted()
    {
        var storage = new InMemoryStorage();
        using var harness = new Harness(storage: storage);
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        storage.FailWriteAfter = 3;
        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Faulted);

        Assert.Equal(1, harness.Power.CallCount);
        var current = harness.Engine.GetSnapshot().CurrentInstance!;
        Assert.Equal(TaskState.Executing, current.State);
        Assert.True(current.HasExecuted);
        await Task.Delay(50);
        Assert.Equal(1, harness.Power.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task H_FailedWriteFailure_KeepsExecuting_NoPower_Faulted()
    {
        var storage = new InMemoryStorage();
        using var harness = new Harness(
            storage: storage,
            configResult: new ConfigurationLoadResult { Status = ConfigurationLoadStatus.Missing, Errors = ["missing"] });
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        storage.FailWriteAfter = 3;
        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Faulted);

        Assert.Equal(0, harness.Power.CallCount);
        var current = harness.Engine.GetSnapshot().CurrentInstance!;
        Assert.Equal(TaskState.Executing, current.State);
        Assert.True(current.HasExecuted);
        await Task.Delay(50);
        Assert.Equal(0, harness.Power.CallCount);
    }

    // ---- I. PowerService 行为 ----

    [Theory(Timeout = 2000)]
    [InlineData(PowerOutcome.Rejected, true)]
    [InlineData(PowerOutcome.Failed, true)]
    [InlineData(PowerOutcome.Unknown, true)]
    [InlineData(PowerOutcome.Simulated, false)]
    public async Task I_NonSimulatedPowerOutcome_EndsFailed_RequestOnce(PowerOutcome outcome, bool wasSimulated)
    {
        using var harness = new Harness(powerResult: new PowerResult
        {
            Outcome = outcome,
            WasSimulated = wasSimulated,
            Message = "unexpected"
        });
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Failed);

        Assert.Equal(TaskState.Failed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(SchedulerEngineStatus.Running, harness.Engine.GetSnapshot().EngineStatus);
        Assert.Equal(1, harness.Power.CallCount);
        await Task.Delay(50);
        Assert.Equal(1, harness.Power.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task I_PowerServiceThrows_EndsFailed_NoRetry()
    {
        using var harness = new Harness(powerCustom: () => throw new InvalidOperationException("Simulated power failure."));
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Failed);

        Assert.Equal(TaskState.Failed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(SchedulerEngineStatus.Running, harness.Engine.GetSnapshot().EngineStatus);
        Assert.Equal(1, harness.Power.CallCount);
        await Task.Delay(50);
        Assert.Equal(1, harness.Power.CallCount);
    }

    [Fact(Timeout = 2000)]
    public async Task I_SimulatedPower_EndsCompleted_RequestOnce()
    {
        using var harness = new Harness();
        var created = await harness.SubmitCreateAsync(CountdownDefinition());
        Assert.True(created.Succeeded);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();

        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Completed);

        Assert.Equal(TaskState.Completed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Equal(SchedulerEngineStatus.Running, harness.Engine.GetSnapshot().EngineStatus);
        Assert.Equal(1, harness.Power.CallCount);
    }

    // ---- J. 每日任务与跨午夜 ----

    [Fact(Timeout = 2000)]
    public async Task J_DailyAt_CrossesMidnight_WithExplicitNonSystemTimeZone()
    {
        var now = new DateTimeOffset(2024, 1, 15, 2, 0, 0, TimeSpan.Zero);
        using var harness = new Harness(utcNow: now, localTimeZone: TestZone);
        var created = await harness.SubmitCreateAsync(DailyAtDefinition());
        Assert.True(created.Succeeded);
        var instance = harness.Engine.GetSnapshot().CurrentInstance!;
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 23, 30, 0, TimeSpan.Zero), instance.ScheduledFireTime);
        Assert.Equal(0, harness.Power.CallCount);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();
        await Task.Delay(50);
        Assert.Equal(0, harness.Power.CallCount);

        harness.Clock.UtcNow = new DateTimeOffset(2024, 1, 15, 23, 30, 0, TimeSpan.Zero);
        harness.Deadline.CompleteNext();
        await WaitUntilAsync(() => harness.Engine.GetSnapshot().CurrentInstance?.State == TaskState.Completed);

        Assert.Equal(1, harness.Power.CallCount);
        Assert.Equal(TaskState.Completed, harness.Engine.GetSnapshot().CurrentInstance!.State);
        Assert.Contains(new DateTimeOffset(2024, 1, 15, 23, 30, 0, TimeSpan.Zero), harness.Deadline.WaitedDeadlines);
    }

    // ---- Helpers ----

    private static TaskDefinition CountdownDefinition(int? warningSeconds = null) => new()
    {
        Id = SourceTaskId,
        Kind = TaskKind.Countdown,
        Action = PowerAction.Shutdown,
        CountdownDuration = TimeSpan.FromHours(2),
        WarningSeconds = warningSeconds,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero)
    };

    private static TaskDefinition DailyAtDefinition() => new()
    {
        Id = SourceTaskId,
        Kind = TaskKind.DailyAt,
        Action = PowerAction.Shutdown,
        TargetTimeOfDay = new TimeOnly(9, 0),
        WarningSeconds = null,
        CreatedAt = new DateTimeOffset(2024, 1, 15, 1, 0, 0, TimeSpan.Zero)
    };

    private static AppConfig ValidConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
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

    private const string WarningRuntimeJson =
        """{"SchemaVersion":1,"CurrentInstance":{"InstanceId":"11111111-1111-1111-1111-111111111111","SourceTaskId":"99999999-9999-9999-9999-999999999999","ActionSnapshot":1,"State":3,"ScheduledFireTime":"2024-01-15T12:00:00+00:00","WarningStartTime":"2024-01-15T11:59:00+00:00","StageToken":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","HasExecuted":false,"CreatedAt":"2024-01-15T10:00:00+00:00"},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private const string ExecutingRuntimeJson =
        """{"SchemaVersion":1,"CurrentInstance":{"InstanceId":"11111111-1111-1111-1111-111111111111","SourceTaskId":"99999999-9999-9999-9999-999999999999","ActionSnapshot":1,"State":4,"ScheduledFireTime":"2024-01-15T12:00:00+00:00","WarningStartTime":null,"StageToken":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","HasExecuted":true,"CreatedAt":"2024-01-15T10:00:00+00:00"},"LastUpdatedAt":"2024-01-15T10:00:00+00:00"}""";

    private const string MissedScheduledRuntimeJson =
        """{"SchemaVersion":1,"CurrentInstance":{"InstanceId":"11111111-1111-1111-1111-111111111111","SourceTaskId":"99999999-9999-9999-9999-999999999999","ActionSnapshot":1,"State":2,"ScheduledFireTime":"2024-01-15T10:00:00+00:00","WarningStartTime":null,"StageToken":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","HasExecuted":false,"CreatedAt":"2024-01-15T09:00:00+00:00"},"LastUpdatedAt":"2024-01-15T09:00:00+00:00"}""";

    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public Harness(
            InMemoryStorage? storage = null,
            AppConfig? config = null,
            ConfigurationLoadResult? configResult = null,
            PowerResult? powerResult = null,
            Func<PowerResult>? powerCustom = null,
            DateTimeOffset? utcNow = null,
            TimeZoneInfo? localTimeZone = null,
            bool startRunning = true)
        {
            Clock = new FakeClock(utcNow ?? ClockBase, localTimeZone);
            Deadline = new ControllableDeadline();
            Storage = storage ?? new InMemoryStorage();
            Configuration = configResult is not null
                ? new StubConfigurationService(configResult)
                : new StubConfigurationService(new ConfigurationLoadResult
                {
                    Status = ConfigurationLoadStatus.Success,
                    Config = config ?? ValidConfig()
                });
            Power = powerCustom is not null
                ? new RecordingPowerService(powerCustom)
                : new RecordingPowerService(powerResult ?? SimulatedResult());

            var stateMachine = new TaskStateMachine();
            var calculator = new NextExecutionCalculator();
            var taskService = new TaskService(
                calculator,
                stateMachine,
                new SequentialIdentifierGenerator(InstanceId1, StageToken1, StageToken2, StageToken3, StageToken2, StageToken3));
            var workflow = new ShutdownWorkflow(Configuration, Power);
            var handler = new ShutdownScheduledTaskHandler(workflow);

            Engine = new SchedulerEngine(
                Storage,
                Clock,
                Deadline,
                taskService,
                stateMachine,
                new SequentialIdentifierGenerator(StageToken1, StageToken2, StageToken3),
                handler);

            RunTask = startRunning ? Engine.RunAsync(_cts.Token) : Task.CompletedTask;
        }

        public FakeClock Clock { get; }

        public ControllableDeadline Deadline { get; }

        public InMemoryStorage Storage { get; }

        public StubConfigurationService Configuration { get; }

        public RecordingPowerService Power { get; }

        public SchedulerEngine Engine { get; }

        public Task RunTask { get; }

        public Guid CreatedInstanceId => Engine.GetSnapshot().CurrentInstance?.InstanceId ?? Guid.Empty;

        public Task<SchedulerCommandResult> SubmitAsync(SchedulerCommand command)
            => Engine.SubmitAsync(command, CancellationToken.None);

        public Task<SchedulerCommandResult> SubmitCreateAsync(TaskDefinition definition)
            => SubmitAsync(new CreateTaskCommand(definition));

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

        private static PowerResult SimulatedResult() => new()
        {
            Outcome = PowerOutcome.Simulated,
            WasSimulated = true,
            Message = "simulated"
        };
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null)
        {
            UtcNow = utcNow;
            LocalTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
        }

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; }
    }

    private sealed class ControllableDeadline : IAsyncDeadline
    {
        private readonly List<DeadlineEntry> _waiters = new();
        private readonly List<DateTimeOffset> _waitedDeadlines = new();
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

        public IReadOnlyList<DateTimeOffset> WaitedDeadlines
        {
            get
            {
                lock (_gate)
                {
                    return _waitedDeadlines.ToList();
                }
            }
        }

        public Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiters.Add(new DeadlineEntry(utcDeadline, tcs));
                _waitedDeadlines.Add(utcDeadline);
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
                    next = _waiters[0].Tcs;
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
                waiters = _waiters.Select(w => w.Tcs).ToList();
                _waiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                waiter.TrySetResult();
            }
        }

        private readonly record struct DeadlineEntry(DateTimeOffset Deadline, TaskCompletionSource Tcs);
    }

    private sealed class InMemoryStorage : IStorage
    {
        private readonly Dictionary<string, string> _documents = new();
        private readonly List<(string Path, string Json)> _writeHistory = new();
        private int _writeCount;

        public int WriteCount => _writeCount;

        public int FailWriteAfter { get; set; } = -1;

        public StorageReadStatus ReadStatus { get; init; } = StorageReadStatus.Success;

        public List<RuntimeState> WrittenRuntimeStates => _writeHistory
            .Where(entry => entry.Path == "runtime.json")
            .Select(entry => JsonSerializer.Deserialize<RuntimeState>(entry.Json)!)
            .ToList();
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

            var json = JsonSerializer.Serialize(value);
            _documents[relativePath] = json;
            _writeHistory.Add((relativePath, json));
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success
            });
        }
    }

    private sealed class StubConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public StubConfigurationService(ConfigurationLoadResult result) => _result = result;

        public int LoadCount { get; private set; }

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(_result);
        }

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPowerService : IPowerService
    {
        private readonly List<PowerRequest> _requests = new();
        private readonly PowerResult? _result;
        private readonly Func<PowerResult>? _custom;

        public RecordingPowerService(PowerResult result) => _result = result;

        public RecordingPowerService(Func<PowerResult> custom) => _custom = custom;

        public IReadOnlyList<PowerRequest> Requests => _requests;

        public int CallCount => _requests.Count;

        public Task<PowerResult> ExecuteAsync(
            PowerRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            if (_custom is not null)
            {
                return Task.FromResult(_custom());
            }

            return Task.FromResult(_result!);
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
}
