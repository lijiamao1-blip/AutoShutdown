namespace AutoShutdown.App.Infrastructure;

public interface IWindowActivationService
{
    void ActivateMainWindow();

    bool IsExiting { get; }

    void MarkExiting();
}
