using AutoShutdown.App.AppHost;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Power;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S12.4.2 task action button regression tests. Verifies that Snooze/Cancel
/// buttons enable for a Scheduled task and that command execution refreshes
/// all buttons. In-memory only (no file IO, no registry, no real power).
/// </summary>
public sealed class S12_4_2TaskActionTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    // ---- 1. Scheduled 活动任务时 Create 禁用 ----

    [Fact]
    public async Task ScheduledTask_CreateIsDisabled()
    {
        var viewModel = CreateViewModel(ScheduledInstance());
        await viewModel.InitializeAsync();
        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        Assert.False(viewModel.CreateCommand.CanExecute(null));
        Assert.False(viewModel.CanCreateNow(out var reason));
        Assert.Equal("已有活动任务", reason);
    }

    // ---- 2. Scheduled 活动任务时 Cancel 启用 ----

    [Fact]
    public void ScheduledTask_CancelIsEnabled()
    {
        var viewModel = CreateViewModel(ScheduledInstance());

        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        Assert.True(viewModel.CanCancel);
        Assert.True(viewModel.CancelCommand.CanExecute(null));
    }

    // ---- 3. Scheduled 活动任务时 Snooze 启用 ----

    [Fact]
    public void ScheduledTask_SnoozeIsEnabled()
    {
        var viewModel = CreateViewModel(ScheduledInstance());

        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        Assert.True(viewModel.CanSnooze);
        Assert.True(viewModel.SnoozeCommand.CanExecute(null));
    }

    // ---- 4. Cancel 成功后 Create 重新启用 ----

    [Fact]
    public async Task CancelSuccess_ReEnablesCreate()
    {
        var viewModel = CreateViewModel(ScheduledInstance(), cancelConfirmation: () => true);
        await viewModel.InitializeAsync();
        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        await viewModel.CancelCommand.ExecuteAsync();

        Assert.True(viewModel.CanCreateNow(out _));
        Assert.True(viewModel.CreateCommand.CanExecute(null));
    }

    // ---- 5. Cancel 成功后当前活动任务清除 ----

    [Fact]
    public async Task CancelSuccess_ClearsCurrentTask()
    {
        var viewModel = CreateViewModel(ScheduledInstance(), cancelConfirmation: () => true);
        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        await viewModel.CancelCommand.ExecuteAsync();

        Assert.False(viewModel.HasCurrentTask);
        Assert.Equal("当前没有活动任务", viewModel.CurrentStateText);
        Assert.False(viewModel.CanCancel);
        Assert.False(viewModel.CanSnooze);
    }

    // ---- 6. Snooze 成功后目标时间增加10分钟 ----

    [Fact]
    public async Task SnoozeSuccess_IncreasesFireTimeBy10Minutes()
    {
        var instance = ScheduledInstance();
        var viewModel = CreateViewModel(instance);
        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        await viewModel.SnoozeCommand.ExecuteAsync();

        var newTime = instance.ScheduledFireTime.AddMinutes(10);
        Assert.Equal(newTime.ToString("yyyy-MM-dd HH:mm:ss"), viewModel.NextFireTimeText);
    }

    // ---- 7. Snooze 成功后 StageToken 更新 ----

    [Fact]
    public async Task SnoozeSuccess_UpdatesStageToken()
    {
        var instance = ScheduledInstance();
        var engine = new FakeSchedulerEngine { Snapshot = SnapshotOf(instance), Now = Now };
        var viewModel = CreateViewModel(engine: engine, cancelConfirmation: () => true);
        viewModel.Refresh(engine.Snapshot, Now);
        var oldToken = instance.StageToken;

        await viewModel.SnoozeCommand.ExecuteAsync();

        var snooze = Assert.IsType<SnoozeTaskCommand>(engine.Commands.Single());
        Assert.Equal(oldToken, snooze.ExpectedStageToken); // 用旧令牌提交
        Assert.NotNull(engine.Snapshot.CurrentInstance);
        Assert.NotEqual(oldToken, engine.Snapshot.CurrentInstance!.StageToken); // 新快照令牌已更新
    }

    // ---- 8. InstanceId 或 StageToken 缺失时按钮禁用并提供原因 ----

    [Fact]
    public void MissingStageToken_DisablesSnooze_WithReason()
    {
        var instance = ScheduledInstance() with { StageToken = Guid.Empty };
        var viewModel = CreateViewModel(instance);

        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        Assert.False(viewModel.CanSnooze);
        Assert.False(viewModel.SnoozeCommand.CanExecute(null));
        Assert.Contains("令牌", viewModel.SnoozeDisabledReason);
    }

    [Fact]
    public void MissingInstanceId_DisablesSnooze_WithReason()
    {
        var instance = ScheduledInstance() with { InstanceId = Guid.Empty };
        var viewModel = CreateViewModel(instance);

        viewModel.Refresh(viewModelEngine(viewModel).Snapshot, Now);

        Assert.False(viewModel.CanSnooze);
        Assert.Contains("标识", viewModel.SnoozeDisabledReason);
    }

    // ---- 9. 命令提交失败时界面显示错误，不得静默 ----

    [Fact]
    public async Task CommandRejected_ShowsError_NotSilent()
    {
        var engine = new FakeSchedulerEngine
        {
            Snapshot = SnapshotOf(ScheduledInstance()),
            Now = Now,
            NextResult = new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.TransitionRejected,
                Snapshot = SnapshotOf(ScheduledInstance()),
                Message = "invalid token"
            }
        };
        var viewModel = CreateViewModel(engine: engine, cancelConfirmation: () => true);
        viewModel.Refresh(engine.Snapshot, Now);

        await viewModel.CancelCommand.ExecuteAsync();

        Assert.Contains("取消任务失败", viewModel.StatusMessage);
        Assert.Contains("取消任务失败", viewModel.RecentActivities[0].Text);
    }

    // ---- 10. IPowerService 通过 GuardedPowerService 解析（默认测试模式走 Fake） ----

    [Fact]
    public void Di_IPowerService_ResolvesToGuardedPowerService()
    {
        var services = new ServiceCollection();
        services.AddAutoShutdownServices(Path.Combine(Path.GetTempPath(), "autoshutdown-s1242-di-" + Guid.NewGuid().ToString("N")));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<GuardedPowerService>(provider.GetRequiredService<IPowerService>());
    }

    // ---- Helpers ----

    private static FakeSchedulerEngine viewModelEngine(MainWindowViewModel viewModel)
        => (FakeSchedulerEngine)viewModel.GetType()
            .GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(viewModel)!;

    private static TaskInstance ScheduledInstance() => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        ActionSnapshot = PowerAction.Shutdown,
        State = TaskState.Scheduled,
        ScheduledFireTime = Now.AddMinutes(30),
        WarningStartTime = Now.AddMinutes(20),
        StageToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        HasExecuted = false,
        CreatedAt = Now
    };

    private static SchedulerSnapshot SnapshotOf(TaskInstance instance) => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        CurrentInstance = instance,
        LastUpdatedAt = Now
    };

    private static MainWindowViewModel CreateViewModel(
        TaskInstance? instance = null,
        FakeSchedulerEngine? engine = null,
        Func<bool>? cancelConfirmation = null)
    {
        engine ??= new FakeSchedulerEngine
        {
            Snapshot = instance is null ? SchedulerSnapshot.Empty : SnapshotOf(instance),
            Now = Now
        };
        var config = new StubConfigurationService(new ConfigurationLoadResult
        {
            Status = ConfigurationLoadStatus.Success,
            Config = new AppConfig
            {
                SchemaVersion = 1,
                TestMode = true,
                AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
                Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
            }
        });
        return new MainWindowViewModel(
            engine,
            config,
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService(),
            cancelConfirmation: cancelConfirmation);
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public DateTimeOffset Now { get; set; }

        public SchedulerCommandResult? NextResult { get; set; }

        public List<SchedulerCommand> Commands { get; } = new();

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);

            if (NextResult is not null)
            {
                var result = NextResult;
                NextResult = null;
                return Task.FromResult(result);
            }

            Snapshot = Apply(command, Snapshot, Now);
            return Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
        }

        public SchedulerSnapshot GetSnapshot() => Snapshot;

        private static SchedulerSnapshot Apply(
            SchedulerCommand command,
            SchedulerSnapshot current,
            DateTimeOffset now) => command switch
        {
            CancelTaskCommand => current with { CurrentInstance = null, LastUpdatedAt = now },
            SnoozeTaskCommand snooze when current.CurrentInstance is not null =>
                current with
                {
                    CurrentInstance = current.CurrentInstance with
                    {
                        StageToken = Guid.NewGuid(),
                        State = TaskState.Scheduled,
                        ScheduledFireTime = current.CurrentInstance.ScheduledFireTime.Add(snooze.Duration),
                        WarningStartTime = null
                    },
                    LastUpdatedAt = now
                },
            CreateTaskCommand create => current with
            {
                CurrentInstance = new TaskInstance
                {
                    InstanceId = create.Definition.Id,
                    SourceTaskId = create.Definition.Id,
                    ActionSnapshot = create.Definition.Action,
                    State = TaskState.Scheduled,
                    ScheduledFireTime = create.Definition.CountdownDuration.HasValue
                        ? now.Add(create.Definition.CountdownDuration.Value)
                        : now,
                    WarningStartTime = create.Definition.WarningSeconds > 0
                        ? now.Add(create.Definition.CountdownDuration ?? TimeSpan.Zero)
                            .AddSeconds(-(create.Definition.WarningSeconds ?? 0))
                        : null,
                    StageToken = Guid.NewGuid(),
                    HasExecuted = false,
                    CreatedAt = now
                },
                LastUpdatedAt = now
            },
            _ => current
        };
    }

    private sealed class StubConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public StubConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });
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
