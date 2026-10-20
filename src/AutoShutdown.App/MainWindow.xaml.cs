using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using AutoShutdown.App.Infrastructure;
using AutoShutdown.App.Presentation;

namespace AutoShutdown.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IWindowActivationService _activationService;

    public MainWindow(MainWindowViewModel viewModel, IWindowActivationService activationService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(activationService);

        InitializeComponent();
        _viewModel = viewModel;
        _activationService = activationService;
        DataContext = _viewModel;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnWindowClosing;

        SkinGalleryPopup.CustomPopupPlacementCallback = PlaceSkinGalleryBelowButtonRightAligned;
        SkinGalleryPopup.Opened += (_, _) => FocusCurrentSkinCard();
        _viewModel.AppearanceSettingsRequested += (_, _) => ScrollAppearanceCardIntoView();
    }

    /// <summary>皮肤画廊锚定在「皮肤」按钮下方、与按钮右边缘对齐（浮层四周留有阴影边距 14）。</summary>
    private static CustomPopupPlacement[] PlaceSkinGalleryBelowButtonRightAligned(Size popupSize, Size targetSize, Point offset)
        => [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 14, targetSize.Height - 6), PopupPrimaryAxis.Horizontal)];

    /// <summary>浮层打开后把键盘焦点放到当前皮肤卡片上，方向键移动、Enter 选择、Esc 关闭。</summary>
    private void FocusCurrentSkinCard()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var current = _viewModel.SkinSection?.CurrentOption;
            var container = current is null ? null : SkinGalleryItems.ItemContainerGenerator.ContainerFromItem(current) as DependencyObject;
            var card = container is null ? null : FindDescendant<Button>(container);
            if (card is not null)
            {
                Keyboard.Focus(card);
            }
        });
    }

    private void ScrollAppearanceCardIntoView()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AppearanceCard.BringIntoView(new Rect(0, 0, 1, 1)));
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var workArea = SystemParameters.WorkArea;
        var logicalLeft = workArea.Left / dpi.DpiScaleX;
        var logicalTop = workArea.Top / dpi.DpiScaleY;
        var logicalWorkWidth = workArea.Width / dpi.DpiScaleX;
        var logicalWorkHeight = workArea.Height / dpi.DpiScaleY;
        var availableWidth = Math.Max(800, logicalWorkWidth * 0.96);
        var availableHeight = Math.Max(540, logicalWorkHeight * 0.94);

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = logicalLeft + Math.Max(0, (logicalWorkWidth - Width) / 2);
        Top = logicalTop + Math.Max(0, (logicalWorkHeight - Height) / 2);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_activationService.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
