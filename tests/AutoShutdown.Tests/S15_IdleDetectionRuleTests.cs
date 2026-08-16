using AutoShutdown.App.Infrastructure.Idle;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Idle;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

public sealed class S15_IdleDetectionRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    // ---- IdleShutdownRule.ResolveThreshold ----

    [Fact]
    public void ResolveThreshold_WhenTaskHasOwnThreshold_UsesTaskThreshold()
    {
        var definition = IdleDefinition(idleThresholdSeconds: 300);

        var resolved = IdleShutdownRule.ResolveThreshold(definition, TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(5), resolved);
    }

    [Fact]
    public void ResolveThreshold_WhenTaskThresholdNull_InheritsGlobalDefault()
    {
        var definition = IdleDefinition(idleThresholdSeconds: null);

        var resolved = IdleShutdownRule.ResolveThreshold(definition, TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(30), resolved);
    }

    [Fact]
    public void ResolveThreshold_WhenBothAbsent_ReturnsNull()
    {
        var definition = IdleDefinition(idleThresholdSeconds: null);

        var resolved = IdleShutdownRule.ResolveThreshold(definition, globalDefault: null);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveThreshold_WhenOwnThresholdInvalid_ReturnsNullInsteadOfFallingBack(int invalidSeconds)
    {
        var definition = IdleDefinition(idleThresholdSeconds: invalidSeconds);

        var resolved = IdleShutdownRule.ResolveThreshold(definition, TimeSpan.FromMinutes(30));

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveThreshold_ThresholdBoundary_OneSecondAccepted()
    {
        var resolved = IdleShutdownRule.ResolveThreshold(
            IdleDefinition(idleThresholdSeconds: 1),
            TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromSeconds(1), resolved);
    }

    // ---- IdleShutdownRule.IsIdleDue ----

    [Fact]
    public void IsIdleDue_WhenDetectionFailed_ReturnsFalse()
    {
        var definition = IdleDefinition(idleThresholdSeconds: 300);

        var due = IdleShutdownRule.IsIdleDue(definition, TimeSpan.FromMinutes(30), idleDuration: null);

        Assert.False(due);
    }

    [Fact]
    public void IsIdleDue_WhenNonIdleKind_ReturnsFalse()
    {
        var definition = new TaskDefinition
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.Countdown,
            Action = PowerAction.Shutdown,
            CountdownDuration = TimeSpan.FromMinutes(5)
        };

        var due = IdleShutdownRule.IsIdleDue(definition, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(40));

        Assert.False(due);
    }

    [Fact]
    public void IsIdleDue_BelowThreshold_ReturnsFalse()
    {
        var due = IdleShutdownRule.IsIdleDue(
            IdleDefinition(idleThresholdSeconds: 300),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(4));

        Assert.False(due);
    }

    [Fact]
    public void IsIdleDue_AtThreshold_ReturnsTrue()
    {
        var due = IdleShutdownRule.IsIdleDue(
            IdleDefinition(idleThresholdSeconds: 300),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5));

        Assert.True(due);
    }

    [Fact]
    public void IsIdleDue_AboveThreshold_ReturnsTrue()
    {
        var due = IdleShutdownRule.IsIdleDue(
            IdleDefinition(idleThresholdSeconds: 300),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(10));

        Assert.True(due);
    }

    [Fact]
    public void IsIdleDue_WhenThresholdMissing_ReturnsFalse()
    {
        var due = IdleShutdownRule.IsIdleDue(
            IdleDefinition(idleThresholdSeconds: null),
            globalDefault: null,
            idleDuration: TimeSpan.FromMinutes(10));

        Assert.False(due);
    }

    // ---- IdleMonitor ----

    [Fact]
    public void IdleMonitor_WhenNotMonitoring_ReturnsNull()
    {
        var monitor = new IdleMonitor(new StubIdleInputSource(TimeSpan.FromMinutes(5)));

        Assert.Null(monitor.GetIdleDuration());
    }

    [Fact]
    public void IdleMonitor_WhenStarted_DelegatesToSource()
    {
        var monitor = new IdleMonitor(new StubIdleInputSource(TimeSpan.FromMinutes(5)));
        monitor.Start();

        Assert.True(monitor.IsMonitoring);
        Assert.Equal(TimeSpan.FromMinutes(5), monitor.GetIdleDuration());

        monitor.Stop();
        Assert.Null(monitor.GetIdleDuration());
    }

    [Fact]
    public void IdleMonitor_WhenSourceFails_ReturnsNull()
    {
        var monitor = new IdleMonitor(new StubIdleInputSource(null));
        monitor.Start();

        Assert.Null(monitor.GetIdleDuration());
    }

    // ---- IdleTickMath（tick 溢出 / 长时间运行） ----

    [Fact]
    public void IdleTickMath_NormalDifference()
    {
        Assert.Equal(500u, IdleTickMath.ComputeIdleMilliseconds(1000, 500));
    }

    [Fact]
    public void IdleTickMath_ZeroIdle()
    {
        Assert.Equal(0u, IdleTickMath.ComputeIdleMilliseconds(1000, 1000));
    }

    [Fact]
    public void IdleTickMath_TickWrap_ComputesCorrectIdle()
    {
        // 32 位 tick 回绕（约 49.7 天）：当前 tick 回绕到 1000，上次输入接近 uint.MaxValue。
        Assert.Equal(1001u, IdleTickMath.ComputeIdleMilliseconds(1000, uint.MaxValue));
    }

    // ---- 结构校验（经 TaskService.Create 公开路径） ----

    [Fact]
    public void Create_ValidIdleTask_WithOwnThreshold_CreatesWaitingInstance()
    {
        var taskService = CreateTaskService();

        var result = taskService.Create(
            IdleDefinition(idleThresholdSeconds: 600, warningSeconds: 60),
            Now,
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Instance);
        Assert.Equal(TaskInstanceState.Waiting, result.Instance!.State);
        Assert.False(result.Instance.IsIdleTriggered);
        Assert.Equal(Now.AddMinutes(10), result.Instance.ScheduledFireTime);
    }

    [Fact]
    public void Create_ValidIdleTask_InheritingGlobal_UsesGlobalDefaultFireTime()
    {
        var taskService = CreateTaskService();

        var result = taskService.Create(
            IdleDefinition(idleThresholdSeconds: null),
            Now,
            TimeZoneInfo.Utc);

        Assert.True(result.Succeeded);
        Assert.Equal(Now.Add(IdleShutdownRule.GlobalDefaultThreshold), result.Instance!.ScheduledFireTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(604801)]
    public void Create_IdleTask_WithInvalidThreshold_Rejected(int invalidSeconds)
    {
        var taskService = CreateTaskService();

        var result = taskService.Create(
            IdleDefinition(idleThresholdSeconds: invalidSeconds),
            Now,
            TimeZoneInfo.Utc);

        Assert.False(result.Succeeded);
        Assert.Equal(TaskCommandStatus.InvalidDefinition, result.Status);
    }

    private static TaskService CreateTaskService() => new(
        new NextExecutionCalculator(),
        new TaskInstanceStateMachine(),
        new GuidIdentifierGenerator());

    private static TaskDefinition IdleDefinition(int? idleThresholdSeconds, int? warningSeconds = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TaskKind.Idle,
        Action = PowerAction.Shutdown,
        IdleThresholdSeconds = idleThresholdSeconds,
        WarningSeconds = warningSeconds,
        CreatedAt = Now
    };

    private sealed class StubIdleInputSource : IIdleInputSource
    {
        private readonly TimeSpan? _idleDuration;

        public StubIdleInputSource(TimeSpan? idleDuration) => _idleDuration = idleDuration;

        public TimeSpan? GetIdleDuration() => _idleDuration;
    }
}
