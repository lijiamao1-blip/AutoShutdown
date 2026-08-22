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

    /// <summary>
    /// S-STARTUP-D1-D1 启动未就绪协议：调用方先应答「已接收」，激活排队到 UI 线程在就绪后
    /// 执行；绝不 <see cref="System.Windows.Threading.Dispatcher.Invoke(System.Action)"/> 同步
    /// 等待被启动步骤阻塞的 UI 线程（否则激活管道会挂起，次实例转发会超时失败）。
    /// 无调度器或已进入退出时为有界失败，静默忽略。
    /// </summary>
    public void ActivateMainWindowDeferred()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        // 排队到 UI 线程队列：UI 就绪（Dispatcher 泵消息）后执行 ActivateMainWindowCore；
        // InvokeAsync 不阻塞调用方。
        dispatcher.InvokeAsync(ActivateMainWindowCore);
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
