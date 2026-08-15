using System.Windows;
using System.Windows.Threading;

namespace AutoShutdown.App.Infrastructure;

public sealed class WindowActivationService : IWindowActivationService
{
    private readonly object _sync = new();
    private readonly IMainWindowFactory _mainWindowFactory;
    private MainWindow? _mainWindow;
    private bool _isExiting;

    public WindowActivationService(IMainWindowFactory mainWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(mainWindowFactory);
        _mainWindowFactory = mainWindowFactory;
    }

    public bool IsExiting
    {
        get
        {
            lock (_sync)
            {
                return _isExiting;
            }
        }
    }

    public void MarkExiting()
    {
        lock (_sync)
        {
            _isExiting = true;
        }
    }

    public void ActivateMainWindow()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            throw new InvalidOperationException("No WPF dispatcher is available.");
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ActivateMainWindowCore());
            return;
        }

        ActivateMainWindowCore();
    }

    private void ActivateMainWindowCore()
    {
        if (IsExiting)
        {
            return;
        }

        lock (_sync)
        {
            _mainWindow ??= _mainWindowFactory.Create();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
    }
}
