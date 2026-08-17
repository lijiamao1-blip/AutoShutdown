using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Tasks;

namespace AutoShutdown.Core.Scheduling.TaskSchedulerSync;

/// <summary>
/// 事件驱动的 outbound 同步协调器（S22 CP3）：消费 <see cref="ITaskService.CollectionChanged"/>
/// （本地事实源增删改/分别启停），防抖合并后调用 <see cref="TaskSyncService"/> 单向同步到外部。
/// 防止同步回环：同步只读本地（GetAll）并只写外部——绝不调用本地任何写操作，外部触发也永不
/// 反向写入本地事实源；同一时刻只允许一个同步在跑（信号量），其余变更在防抖窗口后排队收敛。
/// </summary>
public sealed class TaskSyncCoordinator : IDisposable
{
    private readonly ITaskService _taskService;
    private readonly TaskSyncService _syncService;
    private readonly IClock _clock;
    private readonly string _appExePath;
    private readonly TimeSpan _debounceDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _waiter;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _pendingDebounce;
    private bool _disposed;

    /// <summary>默认防抖窗口：本地连续变更合并为一次同步。</summary>
    public static TimeSpan DefaultDebounceDelay { get; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// 同步开关（默认开启，向后兼容独立使用）。关闭后本地变更的防抖同步一律跳过（绝不创建/
    /// 更新外部任务），从而维持「同步关闭 = 外部无本应用任务，绝不残留」不变量：关闭前由 UI
    /// 先清理外部任务，此门保证清理之后任何本地变更都不会再把外部任务带回来。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>每次同步完成后触发（UI 可据此刷新状态；测试可据此等待）。</summary>
    public event EventHandler<TaskSyncReport>? SyncCompleted;

    public TaskSyncCoordinator(
        ITaskService taskService,
        TaskSyncService syncService,
        IClock clock,
        string appExePath,
        TimeSpan? debounceDelay = null,
        Func<TimeSpan, CancellationToken, Task>? waiter = null)
    {
        ArgumentNullException.ThrowIfNull(taskService);
        ArgumentNullException.ThrowIfNull(syncService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(appExePath);

        if (appExePath.Length == 0)
        {
            throw new ArgumentException("appExePath must not be empty.", nameof(appExePath));
        }

        _taskService = taskService;
        _syncService = syncService;
        _clock = clock;
        _appExePath = appExePath;
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        _waiter = waiter ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        _taskService.CollectionChanged += OnCollectionChanged;
    }

    /// <summary>
    /// 手动触发一次同步（绕过防抖；UI「立即同步」按钮用）。返回本次报告。
    /// 同步已关闭（<see cref="Enabled"/> == false）时跳过：不触碰外部、不触发事件——
    /// 防抖与排空都经此门，保证关闭后外部任务绝不被带回来。
    /// </summary>
    public async Task<TaskSyncReport> SyncNowAsync(CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Enabled)
            {
                return new TaskSyncReport { Succeeded = true };
            }

            var report = await _syncService.SyncAsync(
                _taskService.GetAll(),
                _appExePath,
                _clock.UtcNow,
                _clock.LocalTimeZone,
                cancellationToken).ConfigureAwait(false);

            SyncCompleted?.Invoke(this, report);
            return report;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private void OnCollectionChanged(object? sender, TaskCollectionChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        ScheduleDebouncedSync();
    }

    private void ScheduleDebouncedSync()
    {
        CancellationTokenSource next;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingDebounce?.Cancel();
            _pendingDebounce?.Dispose();
            next = new CancellationTokenSource();
            _pendingDebounce = next;
        }

        _ = DebounceAndSyncAsync(_debounceDelay, next.Token);
    }

    private async Task DebounceAndSyncAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await _waiter(delay, cancellationToken).ConfigureAwait(false);
            }

            await SyncNowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被更新的本地变更取代：旧防抖作废，不作处理。
        }
        catch (Exception)
        {
            // outbound 失败已逐任务体现在报告里；此处不允许破坏防抖循环。
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _taskService.CollectionChanged -= OnCollectionChanged;
            _pendingDebounce?.Cancel();
            _pendingDebounce?.Dispose();
            _pendingDebounce = null;
        }

        _syncGate.Dispose();
    }
}
