using AutoShutdown.App.Infrastructure.AutoStart;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S14 checkpoint 4 规则选择/日期时间/例外日历 UI 切片契约测试。
/// 覆盖：7 种 TimeMode → TaskDefinition 的字段映射、工作日/一次性/节假日的校验、
/// 创建后定义持久化到 tasks.json（含既有任务无损保留）。纯内存，无真实电源操作。
/// </summary>
public sealed class S14_Checkpoint4_CreateRuleTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 11, 0, 0, TimeSpan.Zero);

    // ---- 1. 模式 → TaskKind / 字段映射 ----

    [Fact]
    public void TryBuildDefinition_Countdown_MapsCountdownFields()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Countdown;
        viewModel.CountdownMinutesText = "15 分钟";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.Countdown, definition.Kind);
        Assert.Equal(TimeSpan.FromMinutes(15), definition.CountdownDuration);
        Assert.Null(definition.TargetTimeOfDay);
        Assert.Null(definition.HolidayDates);
    }

    [Fact]
    public void TryBuildDefinition_TodayAt_MapsTargetTime()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.TodayAt;
        viewModel.TimeHoursText = "08 小时";
        viewModel.TimeMinutesText = "30 分钟";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.TodayAt, definition.Kind);
        Assert.Equal(new TimeOnly(8, 30), definition.TargetTimeOfDay);
        Assert.Null(definition.CountdownDuration);
    }

    [Fact]
    public void TryBuildDefinition_DailyAt_MapsDailyAt()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.DailyAt;
        viewModel.TimeHoursText = "22 小时";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.DailyAt, definition.Kind);
        Assert.Equal(new TimeOnly(22, 0), definition.TargetTimeOfDay);
    }

    [Fact]
    public void TryBuildDefinition_NextWorkday_MapsNextWorkday()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.NextWorkday;
        viewModel.TimeHoursText = "09 小时";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.NextWorkday, definition.Kind);
        Assert.Equal(new TimeOnly(9, 0), definition.TargetTimeOfDay);
    }

    [Fact]
    public void TryBuildDefinition_Weekdays_MapsSelectedDays()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdayMonday = false;
        viewModel.WeekdayTuesday = false;

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.Weekdays, definition.Kind);
        Assert.Equal(
            new[] { DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            definition.Weekdays);
    }

    [Fact]
    public void TryBuildDefinition_NthWorkday_MapsIndexPlusOne()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.NthWorkdayOfMonth;
        viewModel.NthWorkdayIndex = 4;

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.NthWorkdayOfMonth, definition.Kind);
        Assert.Equal(5, definition.NthWorkday);
    }

    [Fact]
    public void TryBuildDefinition_OneTime_BuildsDateTimeFromDateAndTime()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.OneTime;
        viewModel.OneTimeDate = new DateTime(2024, 3, 15);
        viewModel.TimeHoursText = "09 小时";
        viewModel.TimeMinutesText = "45 分钟";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(TaskKind.OneTime, definition.Kind);
        Assert.Equal(new DateTime(2024, 3, 15, 9, 45, 0), definition.OneTimeDateTime);
        Assert.Null(definition.TargetTimeOfDay);
    }

    [Fact]
    public void TryBuildDefinition_DailyAt_ParsesHolidayDates()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.DailyAt;
        viewModel.HolidayDatesText = "2024-05-01\r\n2024-10-01";

        Assert.True(viewModel.TryBuildDefinition(out var definition, out _));
        Assert.Equal(
            new[] { new DateOnly(2024, 5, 1), new DateOnly(2024, 10, 1) },
            definition.HolidayDates);
    }

    // ---- 2. 规则字段校验 ----

    [Fact]
    public void TryValidateRuleFields_Weekdays_NoDaySelected_Fails()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.Weekdays;
        viewModel.WeekdayMonday = false;
        viewModel.WeekdayTuesday = false;
        viewModel.WeekdayWednesday = false;
        viewModel.WeekdayThursday = false;
        viewModel.WeekdayFriday = false;

        Assert.False(viewModel.TryValidateRuleFields(out var error));
        Assert.Contains("工作日", error);
    }

    [Fact]
    public void TryValidateRuleFields_OneTime_NoDate_Fails()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.OneTime;

        Assert.False(viewModel.TryValidateRuleFields(out var error));
        Assert.Contains("日期", error);
    }

    [Fact]
    public void TryValidateRuleFields_InvalidHolidayText_Fails()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedMode = TimeMode.DailyAt;
        viewModel.HolidayDatesText = "2024-13-01";

        Assert.False(viewModel.TryValidateRuleFields(out var error));
        Assert.Contains("yyyy-MM-dd", error);
    }

    // ---- 3. 可见性标志随模式切换 ----

    [Fact]
    public void ModeVisibility_FlagsFollowSelectedMode()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedMode = TimeMode.Weekdays;
        Assert.True(viewModel.IsWeekdaySelectorVisible);
        Assert.True(viewModel.IsHolidayInputVisible);

        viewModel.SelectedMode = TimeMode.OneTime;
        Assert.True(viewModel.IsOneTimeDateVisible);
        Assert.False(viewModel.IsHolidayInputVisible);

        viewModel.SelectedMode = TimeMode.NthWorkdayOfMonth;
        Assert.True(viewModel.IsNthWorkdaySelectorVisible);
    }

    // ---- 4. 创建后定义持久化到 tasks.json ----

    [Fact]
    public async Task CreateCommand_PersistsDefinitionToTasksJson()
    {
        var config = new RecordingConfigurationService();
        var viewModel = CreateViewModel(config: config);
        await viewModel.InitializeAsync();

        await viewModel.CreateCommand.ExecuteAsync();

        var saved = Assert.Single(config.SavedDocuments);
        Assert.Equal(TasksDocument.CurrentSchemaVersion, saved.SchemaVersion);
        var task = Assert.Single(saved.Tasks);
        Assert.Equal(TaskKind.Countdown, task.Kind);
        Assert.Equal(PowerAction.Shutdown, task.Action);
    }

    [Fact]
    public async Task CreateCommand_PreservesExistingTasks()
    {
        var existing = new TaskDefinition
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Kind = TaskKind.DailyAt,
            Action = PowerAction.Restart,
            TargetTimeOfDay = new TimeOnly(8, 0),
            WarningSeconds = 60,
            CreatedAt = Now
        };
        var config = new RecordingConfigurationService
        {
            LoadTasksResult = new TasksLoadResult
            {
                Status = TasksLoadStatus.Success,
                Document = new TasksDocument { SchemaVersion = TasksDocument.CurrentSchemaVersion, Tasks = [existing] }
            }
        };
        var viewModel = CreateViewModel(config: config);
        await viewModel.InitializeAsync();

        await viewModel.CreateCommand.ExecuteAsync();

        var saved = Assert.Single(config.SavedDocuments);
        Assert.Equal(2, saved.Tasks.Count);
        Assert.Contains(saved.Tasks, task => task.Id == existing.Id);
    }

    // ---- Helpers ----

    private static MainWindowViewModel CreateViewModel(
        ISchedulerEngine? engine = null,
        IConfigurationService? config = null)
    {
        engine ??= new FakeSchedulerEngine { Snapshot = RunningEmpty() };
        config ??= new RecordingConfigurationService();
        return new MainWindowViewModel(
            engine,
            config,
            new FakeClock(Now),
            new NullLogger(),
            new NoOpAutoStartService());
    }

    private static SchedulerSnapshot RunningEmpty() => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        Instances = new Dictionary<Guid, TaskInstance>(),
        LastUpdatedAt = Now
    };

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } = new()
        {
            EngineStatus = SchedulerEngineStatus.Running,
            Instances = new Dictionary<Guid, TaskInstance>()
        };

        public List<SchedulerCommand> Commands { get; } = new();

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

        public SchedulerSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class RecordingConfigurationService : IConfigurationService
    {
        public ConfigurationLoadResult LoadResult { get; init; } = new()
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

        public TasksLoadResult LoadTasksResult { get; init; } = new() { Status = TasksLoadStatus.NotFound };

        public List<TasksDocument> SavedDocuments { get; } = new();

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(LoadResult);

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationSaveResult { Status = ConfigurationSaveStatus.Success });

        public Task<TasksLoadResult> LoadTasksAsync(CancellationToken cancellationToken)
            => Task.FromResult(LoadTasksResult);

        public Task<TasksSaveResult> SaveTasksAsync(TasksDocument document, CancellationToken cancellationToken)
        {
            SavedDocuments.Add(document);
            return Task.FromResult(new TasksSaveResult { Status = TasksSaveStatus.Success });
        }
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
