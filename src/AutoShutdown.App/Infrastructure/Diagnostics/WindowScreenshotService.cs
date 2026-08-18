using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoShutdown.App.Infrastructure.Logging;

namespace AutoShutdown.App.Infrastructure.Diagnostics;

/// <summary>
/// 仅由用户显式触发的「截图当前窗口」（S-UI1）。流程：弹出 SaveFileDialog → 用户选路径 →
/// RenderTargetBitmap 渲染当前窗口内容 → 写 PNG → 打开所在目录。绝不实现自动上传、远程桌面、
/// 实时监控或后台外传。用户取消或保存失败均如实返回 false，不抛异常。
/// </summary>
public interface IWindowScreenshotService
{
    /// <summary>保存当前窗口截图；成功保存返回 true 并输出保存路径。</summary>
    bool SaveCurrentWindowScreenshot(out string? savedPath);
}

public sealed class WindowScreenshotService : IWindowScreenshotService
{
    private readonly IApplicationLogger _logger;
    private readonly IShellOpenService _shell;
    private readonly Func<Window?> _windowProvider;

    public WindowScreenshotService(
        IApplicationLogger logger,
        IShellOpenService shell,
        Func<Window?> windowProvider)
    {
        _logger = logger;
        _shell = shell;
        _windowProvider = windowProvider;
    }

    public bool SaveCurrentWindowScreenshot(out string? savedPath)
    {
        savedPath = null;

        Window? window;
        try
        {
            window = _windowProvider();
        }
        catch (Exception exception)
        {
            _logger.Error("Screenshot", "无法取得当前窗口", exception);
            return false;
        }

        if (window is null)
        {
            return false;
        }

        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存当前窗口截图（PNG）",
                Filter = "PNG 图片 (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                FileName = $"autoshutdown-window-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };

            if (dialog.ShowDialog(window) != true)
            {
                return false;
            }

            var width = (int)Math.Max(1, Math.Round(window.ActualWidth));
            var height = (int)Math.Max(1, Math.Round(window.ActualHeight));
            var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            render.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(render));

            using (var stream = File.Create(dialog.FileName))
            {
                encoder.Save(stream);
            }

            savedPath = dialog.FileName;
            _shell.OpenFolderAndSelectFile(savedPath);
            _logger.Info("Screenshot", $"窗口截图已保存：{savedPath}");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("Screenshot", "保存窗口截图失败", exception);
            return false;
        }
    }
}
