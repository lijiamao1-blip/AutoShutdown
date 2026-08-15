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
    private readonly HashSet<(Guid InstanceId, Guid StageToken, TaskState State)> _shownKeys = new();

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
            Process(_engine.GetSnapshot().CurrentInstance);
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

    internal void Process(TaskInstance? instance)
    {
        lock (_sync)
        {
            if (instance is null)
            {
                CloseActiveReminder();
                return;
            }

            if (instance.State == TaskState.Warning)
            {
                var key = (instance.InstanceId, instance.StageToken, instance.State);
                if (_shownKeys.Add(key))
                {
                    CloseActiveReminder();
                    _logger.Info(
                        "ReminderShown",
                        "显示关机前提醒，动作：" + instance.ActionSnapshot);
                    _notifications.ShowReminder(instance);
                    _activeReminder = (instance.InstanceId, instance.StageToken);
                }

                return;
            }

            CloseActiveReminder();
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
