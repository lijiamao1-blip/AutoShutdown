using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.PrePipeline;
using AutoShutdown.Core.State;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S16 C2：PrePipelineRunner 串行执行器单元测试。
/// 覆盖执行书必须验证项：注册顺序、串行性、取消、异常隔离、block/continue、空集合。
/// </summary>
public sealed class S16_PrePipelineRunnerTests
{
    private static PrePipelineContext Context() => new()
    {
        InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SourceTaskId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        Action = PowerAction.Shutdown,
        ScheduledFireTime = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero)
    };

    // ---- 空集合 ----

    [Fact]
    public async Task RunAsync_EmptyActionSet_CompletesAndAllowsPower()
    {
        var runner = new PrePipelineRunner(Array.Empty<IPreShutdownAction>());

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Completed, result.Status);
        Assert.True(result.PowerAllowed);
        Assert.Empty(result.Actions);
    }

    // ---- 注册顺序 ----

    [Fact]
    public async Task RunAsync_ExecutesActionsInRegistrationOrder()
    {
        var order = new List<string>();
        var runner = new PrePipelineRunner(
        [
            new RecordingAction("office", FailurePolicy.Continue, order),
            new RecordingAction("commands", FailurePolicy.Continue, order),
            new RecordingAction("closeapps", FailurePolicy.Continue, order)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(["office", "commands", "closeapps"], order);
        Assert.Equal(PrePipelineRunStatus.Completed, result.Status);
        Assert.Equal(3, result.Actions.Count);
    }

    // ---- 串行性：前一个完成前，后一个不得开始 ----

    [Fact]
    public async Task RunAsync_IsSerial_DoesNotStartNextUntilPreviousCompletes()
    {
        var blocker = new BlockingAction();
        var order = new List<string>();
        var follower = new RecordingAction("follower", FailurePolicy.Block, order);
        var runner = new PrePipelineRunner(new IPreShutdownAction[] { blocker, follower });

        var runTask = runner.RunAsync(Context(), CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => blocker.Started));
        Assert.Empty(order); // follower 尚未开始

        blocker.Release();
        var result = await runTask;

        Assert.Equal(["follower"], order);
        Assert.Equal(PrePipelineRunStatus.Completed, result.Status);
    }

    // ---- 审计记录：开始/耗时/结果/名称 ----

    [Fact]
    public async Task RunAsync_RecordsAuditFieldsPerAction()
    {
        var order = new List<string>();
        var runner = new PrePipelineRunner(
        [
            new RecordingAction("a", FailurePolicy.Continue, order),
            new RecordingAction("b", FailurePolicy.Continue, order)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(2, result.Actions.Count);
        foreach (var action in result.Actions)
        {
            Assert.False(string.IsNullOrEmpty(action.ActionName));
            Assert.NotEqual(default, action.StartedAtUtc);
            Assert.True(action.Duration >= TimeSpan.Zero);
            Assert.True(action.Succeeded);
            Assert.Equal(string.Empty, action.ErrorMessage);
        }
    }

    // ---- block：立即停止并取消电源意图 ----

    [Fact]
    public async Task RunAsync_BlockFailure_StopsImmediately_ReturnsBlocked()
    {
        var order = new List<string>();
        var runner = new PrePipelineRunner(
        [
            new RecordingAction("first", FailurePolicy.Continue, order),
            new RecordingAction("blocking", FailurePolicy.Block, order, succeed: false, error: "save failed"),
            new RecordingAction("never", FailurePolicy.Continue, order)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Blocked, result.Status);
        Assert.False(result.PowerAllowed);
        Assert.Equal(["first", "blocking"], order); // "never" 未执行
        Assert.Equal(2, result.Actions.Count);
        Assert.False(result.Actions[1].Succeeded);
        Assert.Equal("save failed", result.Actions[1].ErrorMessage);
    }

    // ---- continue：记录失败但继续，最终允许电源 ----

    [Fact]
    public async Task RunAsync_ContinueFailure_KeepsAuditAndCompletes()
    {
        var order = new List<string>();
        var runner = new PrePipelineRunner(
        [
            new RecordingAction("failing", FailurePolicy.Continue, order, succeed: false, error: "warn"),
            new RecordingAction("later", FailurePolicy.Block, order)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Completed, result.Status);
        Assert.True(result.PowerAllowed);
        Assert.Equal(["failing", "later"], order);
        Assert.Equal(2, result.Actions.Count);
        Assert.False(result.Actions[0].Succeeded);
        Assert.Equal("warn", result.Actions[0].ErrorMessage);
    }

    // ---- 异常隔离：单个动作异常转为 ActionResult，不失控 ----

    [Fact]
    public async Task RunAsync_ActionThrows_IsCapturedAsActionResult()
    {
        var order = new List<string>();
        var runner = new PrePipelineRunner(
        [
            new ThrowingAction("throwing", FailurePolicy.Continue, order),
            new RecordingAction("after", FailurePolicy.Block, order)
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Completed, result.Status); // continue → 后续照常
        Assert.Equal(["throwing", "after"], order);
        Assert.False(result.Actions[0].Succeeded);
        Assert.NotEqual(string.Empty, result.Actions[0].ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_BlockActionThrows_ReturnsBlocked()
    {
        var runner = new PrePipelineRunner(
        [
            new ThrowingAction("throwing-block", FailurePolicy.Block, new List<string>())
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(PrePipelineRunStatus.Blocked, result.Status);
        Assert.False(result.PowerAllowed);
        Assert.False(result.Actions[0].Succeeded);
    }

    // ---- 脱敏：控制字符折叠，防日志注入 ----

    [Fact]
    public async Task RunAsync_SanitizesControlCharactersInError()
    {
        var runner = new PrePipelineRunner(
        [
            new ThrowingAction("inject", FailurePolicy.Block, new List<string>(),
                message: "line1\r\n[FAKE] forged log line")
        ]);

        var result = await runner.RunAsync(Context(), CancellationToken.None);

        Assert.False(result.Actions[0].Succeeded);
        Assert.DoesNotContain("\r", result.Actions[0].ErrorMessage);
        Assert.DoesNotContain("\n", result.Actions[0].ErrorMessage);
    }

    // ---- 取消 ----

    [Fact]
    public async Task RunAsync_WhenPreCancelled_ThrowsBeforeAnyAction()
    {
        var order = new List<string>();
        var runner = new PrePipelineRunner(
        [
            new RecordingAction("a", FailurePolicy.Block, order)
        ]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RunAsync(Context(), cts.Token));

        Assert.Empty(order);
    }

    [Fact]
    public async Task RunAsync_WhenActionThrowsCancellation_Propagates()
    {
        var runner = new PrePipelineRunner(new IPreShutdownAction[] { new CancellingAction() });
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RunAsync(Context(), cts.Token));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start >= timeoutMilliseconds)
            {
                return false;
            }

            await Task.Delay(5);
        }

        return true;
    }

    private sealed class RecordingAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;
        private readonly List<string> _order;
        private readonly bool _succeed;
        private readonly string _error;

        public RecordingAction(
            string name,
            FailurePolicy policy,
            List<string> order,
            bool succeed = true,
            string error = "")
        {
            _name = name;
            _policy = policy;
            _order = order;
            _succeed = succeed;
            _error = error;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _order.Add(_name);
            return Task.FromResult(new PrePipelineActionResult
            {
                Succeeded = _succeed,
                ErrorMessage = _error
            });
        }
    }

    private sealed class ThrowingAction : IPreShutdownAction
    {
        private readonly string _name;
        private readonly FailurePolicy _policy;
        private readonly List<string> _order;
        private readonly string _message;

        public ThrowingAction(
            string name,
            FailurePolicy policy,
            List<string> order,
            string message = "boom")
        {
            _name = name;
            _policy = policy;
            _order = order;
            _message = message;
        }

        public string Name => _name;

        public FailurePolicy FailurePolicy => _policy;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            _order.Add(_name);
            throw new InvalidOperationException(_message);
        }
    }

    private sealed class CancellingAction : IPreShutdownAction
    {
        public string Name => "cancelling";

        public FailurePolicy FailurePolicy => FailurePolicy.Block;

        public Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
            => throw new OperationCanceledException(cancellationToken);
    }

    private sealed class BlockingAction : IPreShutdownAction
    {
        private readonly TaskCompletionSource _release = new();

        public string Name => "blocker";

        public FailurePolicy FailurePolicy => FailurePolicy.Block;

        public bool Started { get; private set; }

        public async Task<PrePipelineActionResult> ExecuteAsync(
            PrePipelineContext context,
            CancellationToken cancellationToken)
        {
            Started = true;
            await _release.Task.WaitAsync(cancellationToken);
            return new PrePipelineActionResult { Succeeded = true };
        }

        public void Release() => _release.TrySetResult();
    }
}
