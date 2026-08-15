using AutoShutdown.App.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace AutoShutdown.App.Infrastructure;

public sealed class MainWindowFactory : IMainWindowFactory
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IServiceProvider _provider;

    public MainWindowFactory(MainWindowViewModel viewModel, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(provider);

        _viewModel = viewModel;
        _provider = provider;
    }

    public MainWindow Create()
    {
        var activationService = _provider.GetRequiredService<IWindowActivationService>();
        return new MainWindow(_viewModel, activationService);
    }
}
