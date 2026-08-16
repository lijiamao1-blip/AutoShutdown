using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S13-T08 UI 切片测试：MainWindowViewModel 任务列表（由 SchedulerSnapshot.Instances
/// 驱动）、分别启停/延迟/清除行命令、8 态文本、空/错状态、恢复通知横幅、
/// 强制冲突询问候选与仲裁结果呈现。UI 只消费服务与快照，不拥有调度循环或持久化。
/// </summary>
public sealed class S13_T08_TaskListUiTests
{
    private static readonly Guid Task1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Task2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Instance1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Instance2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Token1 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Token2 = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ---- 任务列表 ----

    [Fact]
    public void Refresh_PopulatesTaskList_FromSnapshot()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now.AddMinutes(5), PowerAction.Sleep, Instance2, Token2)
        });
        var viewModel = CreateViewModel(engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.True(viewModel.HasTasks);
        Assert.False(viewModel.HasNoTasks);
        Assert.Equal(2, viewModel.TaskItems.Count);
        // 按触发时间升序：Task2（+5 分钟，睡眠）在前，Task1（+30 分钟，关机）在后。
        var first = viewModel.TaskItems[0];
        Assert.Equal(Task2, first.TaskId);
        Assert.Equal("睡眠", first.ActionText);
        Assert.Equal("等待执行", first.StateText);
        Assert.Equal(Instance2, first.InstanceId);
        Assert.Equal(Token2, first.StageToken);
        var second = viewModel.TaskItems[1];
        Assert.Equal(Task1, second.TaskId);
        Assert.Equal("关机", second.ActionText);
        Assert.Equal(Instance1, second.InstanceId);
        Assert.Equal(Token1, second.StageToken);
    }

    [Fact]
    public void Refresh_OrdersByFireTime()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now.AddMinutes(5), PowerAction.Sleep, Instance2, Token2)
        });
        var viewModel = CreateViewModel(engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.Equal(Task2, viewModel.TaskItems[0].TaskId);
        Assert.Equal(Task1, viewModel.TaskItems[1].TaskId);
    }

    [Fact]
    public void Refresh_EmptySnapshot_ShowsEmptyState()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>());
        var viewModel = CreateViewModel(engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.True(viewModel.HasNoTasks);
        Assert.False(viewModel.HasTasks);
        Assert.Empty(viewModel.TaskItems);
    }

    [Fact]
    public void Refresh_RowCapabilities_FollowInstanceState()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now.AddMinutes(5), PowerAction.Sleep, Instance2, Token2)
                with { State = TaskInstanceState.Confirming },
            [Guid.Parse("33333333-3333-3333-3333-333333333333")] = WaitingInstance(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Now.AddMinutes(1),
                PowerAction.Restart,
                Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                Token2) with { State = TaskInstanceState.Executed }
        });
        var viewModel = CreateViewModel(engine);
        viewModel.Refresh(engine.Snapshot, Now);

        var waiting = viewModel.TaskItems.Single(row => row.TaskId == Task1);
        Assert.True(waiting.CanSnooze);
        Assert.True(waiting.CanStop);
        Assert.False(waiting.CanClear);

        var confirming = viewModel.TaskItems.Single(row => row.TaskId == Task2);
        Assert.False(confirming.CanSnooze);
        Assert.True(confirming.CanStop);

        var terminal = viewModel.TaskItems.Single(row => row.State == TaskInstanceState.Executed);
        Assert.True(terminal.CanClear);
        Assert.False(terminal.CanStop);
    }

    [Fact]
    public void Refresh_FaultedEngine_ShowsFaultBanner()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>());
        engine.Snapshot = new SchedulerSnapshot
        {
            EngineStatus = SchedulerEngineStatus.Faulted,
            Instances = new Dictionary<Guid, TaskInstance>(),
            FaultMessage = "boom"
        };
        var viewModel = CreateViewModel(engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.True(viewModel.HasSchedulerFault);
        Assert.Contains("boom", viewModel.SchedulerFaultText);
    }

    // ---- 行命令 ----

    [Fact]
    public void RowSnooze_SubmitsSnoozeCommand_ForThatInstance()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now.AddMinutes(5), PowerAction.Sleep, Instance2, Token2)
        });
        var viewModel = CreateViewModel(engine);
        viewModel.Refresh(engine.Snapshot, Now);

        var row = viewModel.TaskItems.Single(item => item.TaskId == Task2);
        viewModel.RowSnoozeCommand.Execute(row);

        var command = Assert.Single(engine.Commands.OfType<SnoozeTaskCommand>());
        Assert.Equal(Instance2, command.ExpectedInstanceId);
        Assert.Equal(Token2, command.ExpectedStageToken);
        Assert.Equal(TimeSpan.FromMinutes(10), command.Duration);
    }

    [Fact]
    public void RowStop_SubmitsCancelCommand_ForThatInstanceOnly()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now.AddMinutes(5), PowerAction.Sleep, Instance2, Token2)
        });
        var viewModel = CreateViewModel(engine);
        viewModel.Refresh(engine.Snapshot, Now);

        var row = viewModel.TaskItems.Single(item => item.TaskId == Task2);
        viewModel.RowStopCommand.Execute(row);

        var command = Assert.Single(engine.Commands.OfType<CancelTaskCommand>());
        Assert.Equal(Instance2, command.ExpectedInstanceId);
        Assert.Equal(Token2, command.ExpectedStageToken);
    }

    [Fact]
    public void RowClear_SubmitsClearCommand_ForThatInstance()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1)
        });
        var viewModel = CreateViewModel(engine);
        viewModel.Refresh(engine.Snapshot, Now);
        var cleared = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1)
            with { State = TaskInstanceState.Cancelled };

        // 模拟另一轮快照（终态后清除）。
        viewModel.Refresh(new SchedulerSnapshot
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = new Dictionary<Guid, TaskInstance> { [Task1] = cleared },
            LastUpdatedAt = Now
        }, Now);

        var row = viewModel.TaskItems.Single(item => item.TaskId == Task1);
        viewModel.RowClearCommand.Execute(row);

        var command = Assert.Single(engine.Commands.OfType<ClearTerminalTaskCommand>());
        Assert.Equal(Instance1, command.ExpectedInstanceId);
    }

    // ---- 强制冲突询问与仲裁结果 ----

    [Fact]
    public void Refresh_PendingArbitration_PopulatesCandidates()
    {
        var instances = new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now, PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now, PowerAction.Sleep, Instance2, Token2)
        };
        var engine = RunningEngine(instances);
        engine.Snapshot = new SchedulerSnapshot
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = instances,
            LastUpdatedAt = Now,
            PendingArbitration = new PendingArbitration
            {
                CandidateTaskIds = [Task1, Task2],
                DecisionReason = "Forced task(s) conflict."
            }
        };
        var viewModel = CreateViewModel(engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.True(viewModel.HasPendingArbitration);
        Assert.Equal(2, viewModel.PendingArbitrationItems.Count);
        Assert.Contains(viewModel.PendingArbitrationItems, item => item.TaskId == Task1 && item.ActionText == "关机");
        Assert.Contains(viewModel.PendingArbitrationItems, item => item.TaskId == Task2 && item.ActionText == "睡眠");
    }

    [Fact]
    public void ResolveArbitration_SubmitsResolveCommand_ForChosenWinner()
    {
        var instances = new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now, PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now, PowerAction.Sleep, Instance2, Token2)
        };
        var engine = RunningEngine(instances);
        engine.Snapshot = new SchedulerSnapshot
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = instances,
            LastUpdatedAt = Now,
            PendingArbitration = new PendingArbitration
            {
                CandidateTaskIds = [Task1, Task2],
                DecisionReason = "Forced task(s) conflict."
            }
        };
        var viewModel = CreateViewModel(engine);
        viewModel.Refresh(engine.Snapshot, Now);

        var candidate = viewModel.PendingArbitrationItems[0];
        viewModel.ResolveArbitrationCommand.Execute(candidate.TaskId);

        var command = Assert.Single(engine.Commands.OfType<ResolveArbitrationCommand>());
        Assert.Equal(candidate.TaskId, command.WinnerTaskId);
    }

    [Fact]
    public void Refresh_LastArbitration_FormatsDecisionText()
    {
        var instances = new Dictionary<Guid, TaskInstance>
        {
            [Task1] = WaitingInstance(Task1, Now.AddMinutes(30), PowerAction.Shutdown, Instance1, Token1),
            [Task2] = WaitingInstance(Task2, Now.AddMinutes(5), PowerAction.Sleep, Instance2, Token2)
        };
        var engine = RunningEngine(instances);
        engine.Snapshot = new SchedulerSnapshot
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = instances,
            LastUpdatedAt = Now,
            LastArbitration = new ArbitrationOutcome
            {
                WinnerTaskId = Task1,
                RescheduledTaskIds = [Task2],
                DecisionReason = "不同动作：按强度仲裁。"
            }
        };
        var viewModel = CreateViewModel(engine);

        viewModel.Refresh(engine.Snapshot, Now);

        Assert.True(viewModel.HasArbitrationDecision);
        Assert.Contains("赢家执行", viewModel.ArbitrationDecisionText);
        Assert.Contains("改期", viewModel.ArbitrationDecisionText);
        Assert.Contains("不同", viewModel.ArbitrationDecisionText);
    }

    // ---- 恢复通知 ----

    [Fact]
    public async Task InitializeAsync_RecoveryNotice_ShowsBanner()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>());
        var recovery = new RecoveryNoticeService
        {
            Notice = new RecoveryNotice([Task1, Task2])
        };
        var viewModel = CreateViewModel(engine, recovery);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasRecoveryNotice);
        Assert.Contains("2 个任务被标记为中断", viewModel.RecoveryNoticeText);
        Assert.Contains("不会补执行", viewModel.RecoveryNoticeText);
    }

    [Fact]
    public async Task InitializeAsync_NoRecoveryNotice_NoBanner()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>());
        var viewModel = CreateViewModel(engine, new RecoveryNoticeService());
        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasRecoveryNotice);
        Assert.Empty(viewModel.RecoveryNoticeText);
    }

    // ---- 导航 ----

    [Fact]
    public void TasksNav_IsRealPage_NotPlaceholder()
    {
        var engine = RunningEngine(new Dictionary<Guid, TaskInstance>());
        var viewModel = CreateViewModel(engine);

        var tasksNav = viewModel.NavItems.Single(item => item.PageKey == "tasks");
        Assert.Equal("任务管理", tasksNav.Title);
        Assert.False(tasksNav.IsPlaceholder);

        viewModel.SelectedNav = tasksNav;
        Assert.True(viewModel.IsTasksPageVisible);
        Assert.False(viewModel.IsPlaceholderAreaVisible);
        Assert.False(viewModel.IsGenericPlaceholderVisible);
        Assert.False(viewModel.IsHomeVisible);
    }

    // ---- 夹具 ----

    private static MainWindowViewModel CreateViewModel(
        FakeSchedulerEngine engine,
        RecoveryNoticeService? recoveryNotice = null)
    {
        var config = new FakeConfigurationService(new ConfigurationLoadResult
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
            new FakeAutoStartService(),
            autoStartConfirmation: () => true,
            cancelConfirmation: () => true,
            realPowerConfirmation: () => true,
            recoveryNotice: recoveryNotice);
    }

    private static FakeSchedulerEngine RunningEngine(
        IReadOnlyDictionary<Guid, TaskInstance> instances)
        => new()
        {
            Snapshot = new SchedulerSnapshot
            {
                EngineStatus = SchedulerEngineStatus.Running,
                Instances = new Dictionary<Guid, TaskInstance>(instances),
                LastUpdatedAt = Now
            }
        };

    private static TaskInstance WaitingInstance(
        Guid taskId,
        DateTimeOffset fireTime,
        PowerAction action,
        Guid instanceId,
        Guid token) => new()
        {
            InstanceId = instanceId,
            SourceTaskId = taskId,
            ActionSnapshot = action,
            State = TaskInstanceState.Waiting,
            ScheduledFireTime = fireTime,
            WarningStartTime = null,
            StageToken = token,
            HasExecuted = false,
            CreatedAt = Now,
            RealPowerConfirmed = false
        };

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = SchedulerSnapshot.Empty;

        public List<SchedulerCommand> Commands { get; } = new();

        public SchedulerSnapshot GetSnapshot() => Snapshot;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
        }
    }

    private sealed class FakeConfigurationService : IConfigurationService
    {
        private readonly ConfigurationLoadResult _result;

        public FakeConfigurationService(ConfigurationLoadResult result) => _result = result;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public AutoStartStatus Status { get; set; } = AutoStartStatus.Disabled;

        public AutoStartStatus GetStatus() => Status;

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
