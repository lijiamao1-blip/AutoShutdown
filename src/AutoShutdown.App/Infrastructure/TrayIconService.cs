using System.Drawing;
using System.Windows.Forms;
using AutoShutdown.App.Infrastructure.Logging;

namespace AutoShutdown.App.Infrastructure;

public sealed class TrayIconService : IDisposable
{
    /// <summary>与 EXE 图标、主窗口、提醒窗口共用的多尺寸 ICO 资源。</summary>
    private const string IconPackUri = "pack://application:,,,/assets/icon.ico";

    private readonly IWindowActivationService _windowActivation;
    private readonly IApplicationLogger _logger;
    private readonly object _sync = new();
    private NotifyIcon? _notifyIcon;
    private Icon? _icon;

    public TrayIconService(
        IWindowActivationService windowActivation,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(windowActivation);
        ArgumentNullException.ThrowIfNull(logger);
        _windowActivation = windowActivation;
        _logger = logger;
    }

    public Action? ExitRequested { get; set; }

    public void Start()
    {
        lock (_sync)
        {
            if (_notifyIcon is not null)
            {
                return;
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开控制面板", null, (_, _) =>
            {
                _logger.Info("TrayWindowOpened", "托盘菜单：打开控制面板。");
                _windowActivation.ActivateMainWindow();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出程序", null, (_, _) =>
            {
                _logger.Info("TrayExitRequested", "托盘菜单：退出程序。");
                ExitRequested?.Invoke();
            });

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "电脑自动关机助手",
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (_, _) =>
            {
                _logger.Info("TrayWindowOpened", "托盘双击：打开控制面板。");
                _windowActivation.ActivateMainWindow();
            };
        }
    }

    /// <summary>
    /// 托盘气泡提示（S23 CP5 高危提示）。Start 前调用或图标不可用则静默跳过；
    /// 调用方可从任意线程触发，内部经 Dispatcher 回 UI 线程再展示。
    /// </summary>
    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var notifyIcon = _notifyIcon;
        if (notifyIcon is null)
        {
            return;
        }

        Action show = () =>
        {
            try
            {
                notifyIcon.ShowBalloonTip(5000, title, message, icon);
            }
            catch (Exception exception)
            {
                _logger.Warning("TrayBalloonFailed", "托盘气泡提示失败：" + exception.Message);
            }
        };

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            show();
        }
        else
        {
            dispatcher.InvokeAsync(show);
        }
    }

    /// <summary>
    /// 从嵌入资源加载多尺寸 ICO。Icon 构造时会完整读取数据流，
    /// 流可以安全释放；本服务持有 Icon 实例直到退出，避免空白图标。
    /// </summary>
    private Icon LoadAppIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
                new Uri(IconPackUri, UriKind.Absolute))?.Stream;
            if (stream is null)
            {
                _logger.Warning("TrayIconLoadFailed", "托盘图标资源缺失，回退系统默认图标。");
                return SystemIcons.Application;
            }

            using (stream)
            {
                var icon = new Icon(stream);
                _icon = icon;
                return icon;
            }
        }
        catch (Exception exception)
        {
            _logger.Warning(
                "TrayIconLoadFailed",
                "托盘图标加载失败：" + exception.Message);
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        NotifyIcon? notifyIcon;
        Icon? icon;
        lock (_sync)
        {
            notifyIcon = _notifyIcon;
            _notifyIcon = null;
            icon = _icon;
            _icon = null;
        }

        if (notifyIcon is null && icon is null)
        {
            return;
        }

        try
        {
            if (notifyIcon is not null)
            {
                notifyIcon.Visible = false;
                notifyIcon.ContextMenuStrip?.Dispose();
                notifyIcon.Dispose();
            }
        }
        finally
        {
            icon?.Dispose();
        }
    }
}
