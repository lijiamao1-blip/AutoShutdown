using System.Windows;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.State;

namespace AutoShutdown.App.Notifications;

public sealed class WpfNotificationService : INotificationService
{
    private readonly ISchedulerEngine _engine;
    private readonly IClock _clock;
    private readonly IApplicationLogger _logger;
    private readonly object _sync = new();

    private ReminderWindow? _openWindow;
    private Guid _openInstanceId;

    public WpfNotificationService(
        ISchedulerEngine engine,
        IClock clock,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _clock = clock;
        _logger = logger;
    }

    public void ShowReminder(TaskInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        var dispatcher = application.Dispatcher;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ShowReminder(instance));
            return;
        }

        lock (_sync)
        {
            if (_openWindow is not null && _openInstanceId == instance.InstanceId)
            {
                return;
            }

            _openWindow?.Close();
            _openWindow = new ReminderWindow(instance, _engine, _clock, _logger)
            {
                Owner = application.MainWindow
            };
            _openWindow.Closed += OnReminderClosed;
            _openInstanceId = instance.InstanceId;
            _openWindow.Show();
        }
    }

    public void CloseReminder(Guid instanceId)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => CloseReminder(instanceId));
            return;
        }

        lock (_sync)
        {
            if (_openWindow is not null && _openInstanceId == instanceId)
            {
                _openWindow.Close();
            }
        }
    }

    private void OnReminderClosed(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            if (sender is ReminderWindow window)
            {
                window.Closed -= OnReminderClosed;
            }

            _openWindow = null;
            _openInstanceId = Guid.Empty;
        }
    }
}
