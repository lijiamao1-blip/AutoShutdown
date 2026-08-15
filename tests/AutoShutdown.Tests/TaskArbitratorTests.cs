using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S13-T06 TaskArbitrator L2 测试：相同动作合并、不同动作强度/优先级仲裁、
/// 落选改期（≥5 分钟）、强制冲突 RequiresUserDecision、无 IPowerService 依赖。
/// </summary>
public sealed class TaskArbitratorTests
{
    private static readonly Guid TaskA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TaskC = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameAction_MergesIntoSingleWinner()
    {
        var arbitrator = new TaskArbitrator(CreateService(
            Def(TaskA, PowerAction.Shutdown, 50),
            Def(TaskB, PowerAction.Shutdown, 50),
            Def(TaskC, PowerAction.Shutdown, 50)));

        var result = arbitrator.Arbitrate(
            [Due(TaskA, PowerAction.Shutdown), Due(TaskB, PowerAction.Shutdown), Due(TaskC, PowerAction.Shutdown)],
            Now);

        Assert.False(result.RequiresUserDecision);
        Assert.NotNull(result.WinnerTaskId);
        Assert.Equal(2, result.MergedTaskIds.Count);
        Assert.DoesNotContain(result.WinnerTaskId!.Value, result.MergedTaskIds);
        Assert.Empty(result.RescheduledTaskIds);
        Assert.Contains("merge", result.DecisionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameAction_MergedWinnerHasHighestPriority()
    {
        var arbitrator = new TaskArbitrator(CreateService(
            Def(TaskA, PowerAction.Shutdown, 10),
            Def(TaskB, PowerAction.Shutdown, 90),
            Def(TaskC, PowerAction.Shutdown, 50)));

        var result = arbitrator.Arbitrate(
            [Due(TaskA, PowerAction.Shutdown), Due(TaskB, PowerAction.Shutdown), Due(TaskC, PowerAction.Shutdown)],
            Now);

        Assert.Equal(TaskB, result.WinnerTaskId);
        Assert.Equal(new[] { TaskA, TaskC }, result.MergedTaskIds.OrderBy(id => id));
    }

    [Fact]
    public void DifferentActions_StrengthBreaksPriorityTie()
    {
        // 同优先级（默认 0），不同动作：Shutdown > Restart > Hibernate > Sleep。
        var arbitrator = new TaskArbitrator(CreateService(
            Def(TaskA, PowerAction.Sleep, 0),
            Def(TaskB, PowerAction.Shutdown, 0),
            Def(TaskC, PowerAction.Hibernate, 0),
            Def(TaskD(), PowerAction.Restart, 0)));

        var result = arbitrator.Arbitrate(
            [
                Due(TaskA, PowerAction.Sleep),
                Due(TaskB, PowerAction.Shutdown),
                Due(TaskC, PowerAction.Hibernate),
                Due(TaskD(), PowerAction.Restart)
            ],
            Now);

        Assert.Equal(TaskB, result.WinnerTaskId);
        Assert.Contains(TaskA, result.RescheduledTaskIds);
        Assert.Contains(TaskC, result.RescheduledTaskIds);
        Assert.Contains(TaskD(), result.RescheduledTaskIds);
        Assert.Empty(result.MergedTaskIds);
        Assert.False(result.RequiresUserDecision);
    }

    [Fact]
    public void DifferentActions_PriorityBeatsStrength()
    {
        // 低优先级 Shutdown vs 高优先级 Sleep：Priority 优先于动作强度。
        var arbitrator = new TaskArbitrator(CreateService(
            Def(TaskA, PowerAction.Shutdown, 5),
            Def(TaskB, PowerAction.Sleep, 100)));

        var result = arbitrator.Arbitrate(
            [Due(TaskA, PowerAction.Shutdown), Due(TaskB, PowerAction.Sleep)],
            Now);

        Assert.Equal(TaskB, result.WinnerTaskId);
        Assert.Equal(new[] { TaskA }, result.RescheduledTaskIds);
    }

    [Fact]
    public void DifferentActions_LosersGoToRescheduledTaskIds()
    {
        var arbitrator = new TaskArbitrator(CreateService(
            Def(TaskA, PowerAction.Shutdown, 0),
            Def(TaskB, PowerAction.Sleep, 0)));

        var result = arbitrator.Arbitrate(
            [Due(TaskA, PowerAction.Shutdown), Due(TaskB, PowerAction.Sleep)],
            Now);

        Assert.Equal(TaskA, result.WinnerTaskId);
        Assert.Single(result.RescheduledTaskIds);
        Assert.Equal(TaskB, result.RescheduledTaskIds[0]);
    }

    [Fact]
    public void MinimumRescheduleDelay_IsAtLeastFiveMinutes()
    {
        Assert.True(
            TaskArbitrator.MinimumRescheduleDelay >= TimeSpan.FromMinutes(5),
            "Losers must be rescheduled by at least 5 minutes.");
    }

    [Fact]
    public void ForcedConflict_ReturnsRequiresUserDecision()
    {
        var arbitrator = new TaskArbitrator(
            CreateService(
                Def(TaskA, PowerAction.Shutdown, 0),
                Def(TaskB, PowerAction.Sleep, 0)),
            forcedTaskIds: new HashSet<Guid> { TaskB });

        var result = arbitrator.Arbitrate(
            [Due(TaskA, PowerAction.Shutdown), Due(TaskB, PowerAction.Sleep)],
            Now);

        Assert.True(result.RequiresUserDecision);
        Assert.Null(result.WinnerTaskId);
        Assert.Contains("user decision", result.DecisionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleInstance_WinsByDefault()
    {
        var arbitrator = new TaskArbitrator(CreateService(Def(TaskA, PowerAction.Shutdown, 0)));

        var result = arbitrator.Arbitrate([Due(TaskA, PowerAction.Shutdown)], Now);

        Assert.Equal(TaskA, result.WinnerTaskId);
        Assert.False(result.RequiresUserDecision);
        Assert.Empty(result.MergedTaskIds);
        Assert.Empty(result.RescheduledTaskIds);
    }

    [Fact]
    public void NoInstances_ReturnsNoWinner()
    {
        var arbitrator = new TaskArbitrator(CreateService());

        var result = arbitrator.Arbitrate([], Now);

        Assert.Null(result.WinnerTaskId);
        Assert.False(result.RequiresUserDecision);
        Assert.Empty(result.MergedTaskIds);
        Assert.Empty(result.RescheduledTaskIds);
    }

    [Fact]
    public void MissingDefinition_PriorityDefaultsToZero_StrengthDecides()
    {
        // 未注册任务定义（Get 返回 null）→ Priority 回退 0，动作强度决出赢家。
        var arbitrator = new TaskArbitrator(CreateService());

        var result = arbitrator.Arbitrate(
            [Due(TaskA, PowerAction.Sleep), Due(TaskB, PowerAction.Shutdown)],
            Now);

        Assert.Equal(TaskB, result.WinnerTaskId);
    }

    [Fact]
    public void Decorator_LogsDecision_AndPassesThroughUnchanged()
    {
        var inner = new TaskArbitrator(CreateService(
            Def(TaskA, PowerAction.Shutdown, 0),
            Def(TaskB, PowerAction.Sleep, 0)));
        var logger = new RecordingLogger();
        var decorator = new LoggingTaskArbitratorDecorator(inner, logger);

        var result = decorator.Arbitrate(
            [Due(TaskA, PowerAction.Shutdown), Due(TaskB, PowerAction.Sleep)],
            Now);

        // 决策原样透传。
        Assert.Equal(TaskA, result.WinnerTaskId);
        Assert.Equal(new[] { TaskB }, result.RescheduledTaskIds);

        // 审计日志已记录决策。
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("TaskArbitrated", entry.EventName);
        Assert.Contains(result.DecisionReason, entry.Message);
    }

    [Fact]
    public void DoesNotDependOnPowerService()
    {
        // 仲裁器不得持有或注入 IPowerService（I2 唯一电源出口不变）。
        var constructorParameterTypes = typeof(TaskArbitrator)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(param => param.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(IPowerService), constructorParameterTypes);

        var fieldTypes = typeof(TaskArbitrator)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToList();

        Assert.DoesNotContain(typeof(IPowerService), fieldTypes);
    }

    private static Guid TaskD() => Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static TaskDefinition Def(Guid id, PowerAction action, int priority) => new()
    {
        Id = id,
        Kind = TaskKind.Countdown,
        Action = action,
        CountdownDuration = TimeSpan.FromHours(1),
        Priority = priority
    };

    private static TaskInstance Due(Guid taskId, PowerAction action) => new()
    {
        InstanceId = Guid.NewGuid(),
        SourceTaskId = taskId,
        ActionSnapshot = action,
        State = TaskInstanceState.Executing
    };

    private static TaskService CreateService(params TaskDefinition[] definitions)
    {
        var service = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            new GuidIdentifierGenerator());
        foreach (var definition in definitions)
        {
            service.Add(definition);
        }

        return service;
    }

    private sealed record LogEntry(ApplicationLogLevel Level, string EventName, string Message);

    private sealed class RecordingLogger : IApplicationLogger
    {
        public List<LogEntry> Entries { get; } = new();

        public string LogDirectory => string.Empty;

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(
            ApplicationLogLevel level,
            string eventName,
            string message,
            Exception? exception = null)
            => Entries.Add(new LogEntry(level, eventName, message));

        public void Dispose()
        {
        }
    }
}
