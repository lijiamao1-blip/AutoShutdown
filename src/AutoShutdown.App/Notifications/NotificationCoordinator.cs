using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.App.Notifications;

public sealed class NotificationCoordinator : IDisposable
{
    private readonly ISchedulerEngine _engine;
    private readonly INotificationService _notifications;
    private readonly IApplicationLogger _logger;
    private readonly object _sync = new();
    private readonly HashSet<(Guid InstanceId, Guid StageToken, TaskInstanceState State)> _shownKeys = new();

    private (Guid InstanceId, Guid StageToken)? _activeReminder;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startSync = new();

    private Task? _loopTask;
    private bool _started;

    public NotificationCoordinator(
        ISchedulerEngine engine,
        INotificationService notifications,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _notifications = notifications;
        _logger = logger;
    }

    public void Start()
    {
        lock (_startSync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    public void CheckNow()
    {
        try
        {
            Process(_engine.GetSnapshot().Instances.Values);
        }
        catch (Exception)
        {
            // A failed check must never crash the notification loop.
        }
    }

    public void Dispose()
    {
        Task? loopTask;
        lock (_startSync)
        {
            loopTask = _loopTask;
        }

        _cts.Cancel();

        if (loopTask is not null)
        {
            try
            {
                loopTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_sync)
        {
            CloseActiveReminder();
            _shownKeys.Clear();
        }

        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            CheckNow();
        }
    }

    internal void Process(IEnumerable<TaskInstance> instances)
    {
        lock (_sync)
        {
            // 多实例：任一实例进入 Confirming（提醒中）即弹提醒；无提醒实例则收起。
            // 完整列表交互（多实例并排展示）留待 T08 UI 切片。
            var confirming = instances.FirstOrDefault(i => i.State == TaskInstanceState.Confirming);
            if (confirming is null)
            {
                CloseActiveReminder();
                return;
            }

            var key = (confirming.InstanceId, confirming.StageToken, confirming.State);
            if (_shownKeys.Add(key))
            {
                CloseActiveReminder();
                _logger.Info(
                    "ReminderShown",
                    "显示关机前提醒，动作：" + confirming.ActionSnapshot);
                _notifications.ShowReminder(confirming);
                _activeReminder = (confirming.InstanceId, confirming.StageToken);
            }
        }
    }

    private void CloseActiveReminder()
    {
        if (_activeReminder is { } reminder)
        {
            _notifications.CloseReminder(reminder.InstanceId);
            _activeReminder = null;
        }
    }
}
