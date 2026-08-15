using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S13 集成缺陷回归：L4 冒烟发现 WPF 启动线程上 `RecoverAsync(...).GetResult()`
/// 与存储读取 async I/O 之间的 async-over-sync 死锁。存储读取必须不捕获
/// SynchronizationContext（ConfigureAwait(false)），否则应用在
/// 「调度引擎启动中」后永久挂起。
/// </summary>
public sealed class S13_L4_StartupDeadlockRegressionTests
{
    [Fact]
    public void RuntimeStateStoreLoad_OnBlockedSynchronizationContext_DoesNotDeadlock()
    {
        var root = Path.Combine(Path.GetTempPath(), "autoshutdown-deadlock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuntimeStateStore(new FileStorage(root));
            BlockingSave(store, new RuntimeState
            {
                SchemaVersion = RuntimeState.CurrentSchemaVersion,
                Instances = new Dictionary<Guid, TaskInstance>(),
                LastUpdatedAt = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero)
            });

            var original = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new ThrowingSynchronizationContext());
            try
            {
                // 模拟崩溃恢复在「UI 线程」上的同步等待：若 LoadAsync 链中任何 await
                // 捕获当前上下文，continuation 会被 Post 回已阻塞的调度器而抛错；
                // 全部 ConfigureAwait(false) 后完成于线程池，测试通过。
                var load = BlockingLoad(store);
                Assert.Equal(RuntimeStateLoadStatus.Success, load.Status);
                Assert.Empty(load.State!.Instances);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // 阻塞式辅助（模拟 ApplicationLifetimeCoordinator.RecoverFromCrash 的调用形态），
    // 避免 xUnit 分析器对测试方法的阻塞调用告警。
    private static RuntimeStateSaveResult BlockingSave(RuntimeStateStore store, RuntimeState state)
        => store.SaveAsync(state, CancellationToken.None).GetAwaiter().GetResult();

    private static RuntimeStateLoadResult BlockingLoad(RuntimeStateStore store)
        => store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 模拟 WPF UI SynchronizationContext：任何 Post/Send 都抛错。若 await 捕获
    /// 该上下文，continuation 的调度会立即抛错，测试失败；若全程
    /// ConfigureAwait(false)，永不触碰该上下文，测试通过。
    /// </summary>
    private sealed class ThrowingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("Continuation posted to captured context (deadlock).");

        public override void Send(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("Continuation sent to captured context (deadlock).");
    }

    private sealed class TestDocument
    {
        public string Name { get; set; } = string.Empty;

        public string Blob { get; set; } = string.Empty;
    }
}
