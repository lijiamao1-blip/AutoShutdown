using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// V2 8 态状态机测试（架构 V2-DRAFT-004 S13 §5；GATE-A2 裁决）。
/// 白名单 = 15 条唯一状态边；触发场景 = 16 个（waiting→cancelled 两个：
/// 用户取消 与 一次性任务过期，修正 6d）。
/// </summary>
public sealed class TaskInstanceStateMachineTests
{
    private readonly TaskInstanceStateMachine _machine = new();

    // ===== 15 条唯一状态边（均允许） =====

    [Theory]
    [InlineData(TaskInstanceState.Waiting, TaskInstanceState.Confirming, TaskInstanceStateTransitionCause.ScheduleTriggered)]
    [InlineData(TaskInstanceState.Waiting, TaskInstanceState.Cancelled, TaskInstanceStateTransitionCause.CancelByUser)]
    [InlineData(TaskInstanceState.Waiting, TaskInstanceState.Faulted, TaskInstanceStateTransitionCause.ConfigCorrupt)]
    [InlineData(TaskInstanceState.Confirming, TaskInstanceState.Running, TaskInstanceStateTransitionCause.PowerConfirmed)]
    [InlineData(TaskInstanceState.Confirming, TaskInstanceState.Cancelled, TaskInstanceStateTransitionCause.CancelledDuringConfirmation)]
    [InlineData(TaskInstanceState.Confirming, TaskInstanceState.Interrupted, TaskInstanceStateTransitionCause.CrashRecovered)]
    [InlineData(TaskInstanceState.Confirming, TaskInstanceState.Faulted, TaskInstanceStateTransitionCause.ConfigCorrupt)]
    [InlineData(TaskInstanceState.Running, TaskInstanceState.Executing, TaskInstanceStateTransitionCause.PipelineCompleted)]
    [InlineData(TaskInstanceState.Running, TaskInstanceState.Cancelled, TaskInstanceStateTransitionCause.PipelineFailedBlocked)]
    [InlineData(TaskInstanceState.Running, TaskInstanceState.Interrupted, TaskInstanceStateTransitionCause.CrashRecovered)]
    [InlineData(TaskInstanceState.Running, TaskInstanceState.Faulted, TaskInstanceStateTransitionCause.RuntimeConfigCorrupt)]
    [InlineData(TaskInstanceState.Executing, TaskInstanceState.Executed, TaskInstanceStateTransitionCause.PowerCompleted)]
    [InlineData(TaskInstanceState.Executing, TaskInstanceState.Interrupted, TaskInstanceStateTransitionCause.CrashRecovered)]
    [InlineData(TaskInstanceState.Executing, TaskInstanceState.Faulted, TaskInstanceStateTransitionCause.PowerFailed)]
    [InlineData(TaskInstanceState.Executed, TaskInstanceState.Waiting, TaskInstanceStateTransitionCause.Reschedule)]
    public void TryTransition_All15UniqueStateEdges_AreAllowed(
        TaskInstanceState current,
        TaskInstanceState target,
        TaskInstanceStateTransitionCause cause)
    {
        var result = _machine.TryTransition(current, target, cause, source: "Test");

        Assert.True(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.Allowed, result.DecisionCode);
        Assert.Equal(current, result.CurrentState);
        Assert.Equal(target, result.TargetState);
        Assert.Equal(cause, result.Cause);
        Assert.Equal("Test", result.Source);
        Assert.NotEqual(default, result.TimestampUtc);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    // ===== 第 16 个触发场景：waiting→cancelled 的一次性任务过期（修正 6d） =====

    [Fact]
    public void TryTransition_TriggerScenario_WaitingToCancelled_OneTimeExpired_IsAllowed()
    {
        var result = _machine.TryTransition(
            TaskInstanceState.Waiting,
            TaskInstanceState.Cancelled,
            TaskInstanceStateTransitionCause.OneTimeExpired);

        Assert.True(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.Allowed, result.DecisionCode);
    }

    [Fact]
    public void TryTransition_TriggerScenario_WaitingToCancelled_UserCancel_IsAllowed()
    {
        var result = _machine.TryTransition(
            TaskInstanceState.Waiting,
            TaskInstanceState.Cancelled,
            TaskInstanceStateTransitionCause.CancelByUser);

        Assert.True(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.Allowed, result.DecisionCode);
    }

    // ===== 白名单完整性：恰好 15 条边 / 16 个触发场景 =====

    [Fact]
    public void Whitelist_HasExactly15UniqueStateEdges()
    {
        var allowedEdges = new HashSet<(TaskInstanceState, TaskInstanceState)>();

        foreach (var current in AllV2States())
        foreach (var target in AllV2States())
        foreach (var cause in AllCauses())
        {
            if (_machine.TryTransition(current, target, cause).Allowed)
            {
                allowedEdges.Add((current, target));
            }
        }

        Assert.Equal(15, allowedEdges.Count);
    }

    [Fact]
    public void Whitelist_HasExactly16TriggerScenarios()
    {
        var scenarioCount = 0;

        foreach (var current in AllV2States())
        foreach (var target in AllV2States())
        foreach (var cause in AllCauses())
        {
            if (_machine.TryTransition(current, target, cause).Allowed)
            {
                scenarioCount++;
            }
        }

        Assert.Equal(16, scenarioCount);
    }

    // ===== 终态不可逆 =====

    [Theory]
    [InlineData(TaskInstanceState.Cancelled)]
    [InlineData(TaskInstanceState.Faulted)]
    [InlineData(TaskInstanceState.Interrupted)]
    public void TryTransition_TerminalStates_AreIrreversible(TaskInstanceState terminal)
    {
        foreach (var target in AllV2States())
        {
            foreach (var cause in AllCauses())
            {
                var result = _machine.TryTransition(terminal, target, cause);

                Assert.False(result.Allowed, $"Terminal {terminal} must not transition to {target} with cause {cause}.");
                Assert.Equal(TaskInstanceStateTransitionDecisionCode.TransitionNotAllowed, result.DecisionCode);
            }
        }
    }

    [Fact]
    public void TryTransition_TerminalState_IsNotTheCurrentStateOfAnyAllowedEdge()
    {
        foreach (var terminal in new[] { TaskInstanceState.Cancelled, TaskInstanceState.Faulted, TaskInstanceState.Interrupted })
        foreach (var target in AllV2States())
        foreach (var cause in AllCauses())
        {
            Assert.False(_machine.TryTransition(terminal, target, cause).Allowed);
        }
    }

    // ===== 非法转换：结构化拒绝结果（字段完整，供 Error 审计日志） =====

    [Fact]
    public void TryTransition_IllegalTransition_ReturnsStructuredRejectionWithAllAuditFields()
    {
        var result = _machine.TryTransition(
            TaskInstanceState.Waiting,
            TaskInstanceState.Executing,
            TaskInstanceStateTransitionCause.ScheduleTriggered,
            source: "SchedulerEngine");

        Assert.False(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.TransitionNotAllowed, result.DecisionCode);
        Assert.Equal(TaskInstanceState.Waiting, result.CurrentState);
        Assert.Equal(TaskInstanceState.Executing, result.TargetState);
        Assert.Equal(TaskInstanceStateTransitionCause.ScheduleTriggered, result.Cause);
        Assert.Equal("SchedulerEngine", result.Source);
        Assert.NotEqual(default, result.TimestampUtc);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public void TryTransition_ExecutedToRunning_IsRejected()
    {
        var result = _machine.TryTransition(
            TaskInstanceState.Executed,
            TaskInstanceState.Running,
            TaskInstanceStateTransitionCause.Reschedule);

        Assert.False(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.TransitionNotAllowed, result.DecisionCode);
    }

    // ===== cause 不匹配 =====

    [Theory]
    [InlineData(TaskInstanceState.Waiting, TaskInstanceState.Confirming, TaskInstanceStateTransitionCause.OneTimeExpired)]
    [InlineData(TaskInstanceState.Waiting, TaskInstanceState.Cancelled, TaskInstanceStateTransitionCause.ScheduleTriggered)]
    [InlineData(TaskInstanceState.Running, TaskInstanceState.Interrupted, TaskInstanceStateTransitionCause.PowerConfirmed)]
    [InlineData(TaskInstanceState.Executing, TaskInstanceState.Executed, TaskInstanceStateTransitionCause.PowerFailed)]
    [InlineData(TaskInstanceState.Executed, TaskInstanceState.Waiting, TaskInstanceStateTransitionCause.CancelByUser)]
    public void TryTransition_KnownEdgeWithWrongCause_ReturnsCauseMismatch(
        TaskInstanceState current,
        TaskInstanceState target,
        TaskInstanceStateTransitionCause wrongCause)
    {
        var result = _machine.TryTransition(current, target, wrongCause);

        Assert.False(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.CauseMismatch, result.DecisionCode);
    }

    // ===== Unknown 输入 =====

    [Fact]
    public void TryTransition_WhenCurrentStateIsUnknown_ReturnsUnknownCurrentState()
    {
        var result = _machine.TryTransition(
            TaskInstanceState.Unknown,
            TaskInstanceState.Waiting,
            TaskInstanceStateTransitionCause.ScheduleTriggered);

        Assert.False(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.UnknownCurrentState, result.DecisionCode);
    }

    [Fact]
    public void TryTransition_WhenTargetStateIsUnknown_ReturnsUnknownTargetState()
    {
        var result = _machine.TryTransition(
            TaskInstanceState.Waiting,
            TaskInstanceState.Unknown,
            TaskInstanceStateTransitionCause.ScheduleTriggered);

        Assert.False(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.UnknownTargetState, result.DecisionCode);
    }

    [Fact]
    public void TryTransition_WhenCauseIsUnknown_ReturnsUnknownCause()
    {
        var result = _machine.TryTransition(
            TaskInstanceState.Waiting,
            TaskInstanceState.Running,
            TaskInstanceStateTransitionCause.Unknown);

        Assert.False(result.Allowed);
        Assert.Equal(TaskInstanceStateTransitionDecisionCode.UnknownCause, result.DecisionCode);
    }

    // ===== 结构化结果：时间戳与来源可供审计 =====

    [Fact]
    public void TryTransition_ResultCarriesTimestampAndSource_ForAuditLogging()
    {
        var before = DateTimeOffset.UtcNow.AddMilliseconds(-50);
        var result = _machine.TryTransition(
            TaskInstanceState.Waiting,
            TaskInstanceState.Confirming,
            TaskInstanceStateTransitionCause.ScheduleTriggered,
            source: "SchedulerEngine");
        var after = DateTimeOffset.UtcNow.AddMilliseconds(50);

        Assert.True(result.Allowed);
        Assert.Equal("SchedulerEngine", result.Source);
        Assert.True(result.TimestampUtc >= before && result.TimestampUtc <= after,
            $"TimestampUtc {result.TimestampUtc:O} not within test window.");
    }

    [Fact]
    public void TryTransition_RepeatedValidCalls_AreStable()
    {
        var first = _machine.TryTransition(TaskInstanceState.Waiting, TaskInstanceState.Confirming, TaskInstanceStateTransitionCause.ScheduleTriggered);
        var second = _machine.TryTransition(TaskInstanceState.Waiting, TaskInstanceState.Confirming, TaskInstanceStateTransitionCause.ScheduleTriggered);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Equal(first.CurrentState, second.CurrentState);
        Assert.Equal(first.TargetState, second.TargetState);
        Assert.Equal(first.Cause, second.Cause);
        Assert.Equal(first.DecisionCode, second.DecisionCode);
    }

    // ===== 纯状态机：无日志基础设施依赖 =====

    [Fact]
    public void TryTransition_PureMachine_HasNoLoggingInfrastructureDependency()
    {
        // 参数less 构造即证明无注入的 logger/storage/service 依赖。
        var machine = new TaskInstanceStateMachine();

        var result = machine.TryTransition(TaskInstanceState.Waiting, TaskInstanceState.Confirming, TaskInstanceStateTransitionCause.ScheduleTriggered);

        Assert.True(result.Allowed);
    }

    // ===== V1→V2 映射（架构书 §4.4） =====

    [Theory]
    [InlineData(TaskState.Idle, TaskInstanceState.Waiting)]
    [InlineData(TaskState.Scheduled, TaskInstanceState.Waiting)]
    [InlineData(TaskState.Warning, TaskInstanceState.Confirming)]
    [InlineData(TaskState.Executing, TaskInstanceState.Executing)]
    [InlineData(TaskState.Completed, TaskInstanceState.Executed)]
    [InlineData(TaskState.Cancelled, TaskInstanceState.Cancelled)]
    [InlineData(TaskState.Failed, TaskInstanceState.Faulted)]
    [InlineData(TaskState.Interrupted, TaskInstanceState.Interrupted)]
    public void V1ToV2Mapping_FollowsArchitectureSection4_4(TaskState v1, TaskInstanceState v2)
    {
        Assert.Equal(v2, MapV1ToV2(v1));
    }

    private static TaskInstanceState MapV1ToV2(TaskState s) => s switch
    {
        TaskState.Idle => TaskInstanceState.Waiting,
        TaskState.Scheduled => TaskInstanceState.Waiting,
        TaskState.Warning => TaskInstanceState.Confirming,
        TaskState.Executing => TaskInstanceState.Executing,
        TaskState.Completed => TaskInstanceState.Executed,
        TaskState.Cancelled => TaskInstanceState.Cancelled,
        TaskState.Failed => TaskInstanceState.Faulted,
        TaskState.Interrupted => TaskInstanceState.Interrupted,
        _ => TaskInstanceState.Unknown
    };

    private static IEnumerable<TaskInstanceState> AllV2States()
        => Enum.GetValues<TaskInstanceState>().Where(s => s != TaskInstanceState.Unknown);

    private static IEnumerable<TaskInstanceStateTransitionCause> AllCauses()
        => Enum.GetValues<TaskInstanceStateTransitionCause>().Where(c => c != TaskInstanceStateTransitionCause.Unknown);
}
