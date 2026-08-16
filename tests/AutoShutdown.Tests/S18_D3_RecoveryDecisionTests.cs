using System;
using System.Threading;
using AutoShutdown.App.Infrastructure.CloseApps;
using AutoShutdown.Core.CloseApps;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S18-D3 返修回归：直接验证真实网关 <see cref="DiagnosticAppWindowManager"/> 的等待异常
/// 恢复判断与恢复轮询（<see cref="DiagnosticAppWindowManager.DecideRecoveryAfterWaitException"/> /
/// <see cref="DiagnosticAppWindowManager.RecoverAfterWaitException"/>）。核心契约：
/// 等待查询发生异常 ≠ 优雅等待期限届满——复核 Running 且期限未到绝不提前 TimedOut（绝不提前
/// 强杀）、继续有界轮询；Exited 立即返回；Unknown fail-closed；只有明确到达期限且仍运行才
/// TimedOut；取消以 OCE 原样传播。全部注入假复核源/时钟/延迟，快速且确定；不启动/关闭真实
/// 进程，不触碰真实电源。
/// </summary>
public sealed class S18_D3_RecoveryDecisionTests
{
    /// <summary>固定基准时间：配合注入时钟使期限判断确定。</summary>
    private static readonly DateTimeOffset Base =
        new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ---- 恢复判断（纯决策：复核状态 × 期限状态） ----

    [Fact]
    public void Decision_RecheckRunning_DeadlineNotReached_IsContinuePolling_NotTimedOut()
    {
        // D3-1：复核 Running 且期限未到 → 继续有界轮询，绝不提前 TimedOut。
        var decision = DiagnosticAppWindowManager.DecideRecoveryAfterWaitException(
            ProcessExitStatus.Running, deadlineReached: false);

        Assert.Equal(WaitRecoveryDecision.ContinuePolling, decision);
        Assert.NotEqual(WaitRecoveryDecision.TimedOut, decision);
    }

    [Fact]
    public void Decision_RecheckRunning_DeadlineReached_IsTimedOut()
    {
        // D3-3：只有明确到达期限且仍运行，才允许判定超时。
        Assert.Equal(
            WaitRecoveryDecision.TimedOut,
            DiagnosticAppWindowManager.DecideRecoveryAfterWaitException(
                ProcessExitStatus.Running, deadlineReached: true));
    }

    [Fact]
    public void Decision_RecheckExited_IsExited_RegardlessOfDeadline()
    {
        // D3-2：复核已退出 → 立即 Exited（期限是否到达无关；不得继续等待、不得强杀）。
        Assert.Equal(
            WaitRecoveryDecision.Exited,
            DiagnosticAppWindowManager.DecideRecoveryAfterWaitException(
                ProcessExitStatus.Exited, deadlineReached: false));
        Assert.Equal(
            WaitRecoveryDecision.Exited,
            DiagnosticAppWindowManager.DecideRecoveryAfterWaitException(
                ProcessExitStatus.Exited, deadlineReached: true));
    }

    [Fact]
    public void Decision_RecheckUnknown_IsUnknown_RegardlessOfDeadline()
    {
        // D3-5：复核无法确认 → Unknown（fail-closed），绝不当作超时或成功。
        Assert.Equal(
            WaitRecoveryDecision.Unknown,
            DiagnosticAppWindowManager.DecideRecoveryAfterWaitException(
                ProcessExitStatus.Unknown, deadlineReached: false));
        Assert.Equal(
            WaitRecoveryDecision.Unknown,
            DiagnosticAppWindowManager.DecideRecoveryAfterWaitException(
                ProcessExitStatus.Unknown, deadlineReached: true));
    }

    // ---- 恢复轮询（有界、可取消） ----

    [Fact]
    public void Recover_RunningBeforeDeadline_ContinuesBoundedPolling_NoPrematureTimeout()
    {
        // D3-1 轮询：复核持续 Running 且期限未到 → 继续有界轮询，绝不提前 TimedOut。
        // 用「第 3 轮有界延迟抛出哨兵」证明循环持续到第 3 轮仍在等待（未提前返回超时）。
        var sentinel = new InvalidOperationException("still waiting; not a premature timeout");
        var reads = 0;
        var delays = 0;

        ProcessExitStatus ReadStatus(int _)
        {
            reads++;
            return ProcessExitStatus.Running;
        }

        void Delay(TimeSpan _)
        {
            delays++;
            if (delays >= 3)
            {
                throw sentinel;
            }
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DiagnosticAppWindowManager.RecoverAfterWaitException(
                ReadStatus,
                processId: 100,
                deadline: Base.AddMinutes(30),
                clock: () => Base,
                Delay,
                CancellationToken.None));

        Assert.Same(sentinel, exception);
        Assert.True(delays >= 3, "期限未到必须持续有界轮询，而不是提前返回 TimedOut。");
        Assert.True(reads >= delays, "每轮应复核一次退出状态。");
    }

    [Fact]
    public void Recover_ExitsAfterContinuedWait_ReturnsExited()
    {
        // D3-2 轮询：等待异常后继续有界轮询，进程随后退出 → Exited（绝不提前 TimedOut/强杀）。
        var reads = 0;
        var delays = 0;

        ProcessExitStatus ReadStatus(int _)
            => ++reads >= 3 ? ProcessExitStatus.Exited : ProcessExitStatus.Running;

        void Delay(TimeSpan _) => delays++;

        var result = DiagnosticAppWindowManager.RecoverAfterWaitException(
            ReadStatus,
            processId: 100,
            deadline: Base.AddMinutes(30),
            clock: () => Base,
            Delay,
            CancellationToken.None);

        Assert.Equal(ProcessWaitResult.Exited, result);
        Assert.True(delays >= 1, "进程退出前应先经过有界等待，而非立即判定。");
    }

    [Fact]
    public void Recover_RunningUntilDeadline_ThenReturnsTimedOut()
    {
        // D3-3 轮询：复核持续 Running，每轮有界延迟推进 100ms，期限 1000ms 后到达 →
        // 只有到达期限后才返回 TimedOut（绝不提前）。
        var now = Base;

        ProcessExitStatus ReadStatus(int _) => ProcessExitStatus.Running;

        void Delay(TimeSpan _) => now = now.AddMilliseconds(100);

        var deadline = Base.AddMilliseconds(1000);
        var result = DiagnosticAppWindowManager.RecoverAfterWaitException(
            ReadStatus,
            processId: 100,
            deadline,
            clock: () => now,
            Delay,
            CancellationToken.None);

        Assert.Equal(ProcessWaitResult.TimedOut, result);
        Assert.True(now >= deadline, "TimedOut 只能在到达期限后产生。");
    }

    [Fact]
    public void Recover_CancellationDuringRecovery_PropagatesOce()
    {
        // D3-4：等待异常后继续轮询期间取消 → OCE 原样传播，绝不返回 TimedOut/Unknown。
        using var cts = new CancellationTokenSource();
        var delays = 0;

        ProcessExitStatus ReadStatus(int _) => ProcessExitStatus.Running;

        void Delay(TimeSpan _)
        {
            delays++;
            if (delays == 2)
            {
                cts.Cancel();
            }
        }

        Assert.ThrowsAny<OperationCanceledException>(() =>
            DiagnosticAppWindowManager.RecoverAfterWaitException(
                ReadStatus,
                processId: 100,
                deadline: Base.AddMinutes(30),
                clock: () => Base,
                Delay,
                cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.True(delays >= 2, "取消应在继续轮询期间到达。");
    }

    [Fact]
    public void Recover_RecheckUnknown_ReturnsUnknown_FailClosed()
    {
        // D3-5 轮询：等待异常后复核 Unknown → Unknown（fail-closed），绝不当作超时。
        var result = DiagnosticAppWindowManager.RecoverAfterWaitException(
            _ => ProcessExitStatus.Unknown,
            processId: 100,
            deadline: Base.AddMinutes(30),
            clock: () => Base,
            _ => { },
            CancellationToken.None);

        Assert.Equal(ProcessWaitResult.Unknown, result);
    }
}
