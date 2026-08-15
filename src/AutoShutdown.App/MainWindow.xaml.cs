using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
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
