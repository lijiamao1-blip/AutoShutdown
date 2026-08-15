using System.Windows.Threading;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.App.Presentation;

public sealed class DashboardRefreshService : IDisposable
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ISchedulerEngine _engine;
    private readonly IClock _clock;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();

    private Task? _loopTask;
    private bool _started;

    public DashboardRefreshService(
        MainWindowViewModel viewModel,
        ISchedulerEngine engine,
        IClock clock,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _viewModel = viewModel;
        _engine = engine;
        _clock = clock;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    public void RefreshOnce()
    {
        _dispatcher.Invoke(() =>
        {
            try
            {
                _viewModel.Refresh(_engine.GetSnapshot(), _clock.UtcNow);
            }
            catch (Exception)
            {
                // A failed refresh must not crash the UI loop.
            }
        });
    }

    public void Dispose()
    {
        Task? loopTask;
        lock (_sync)
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

        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var snapshot = _engine.GetSnapshot();
                _dispatcher.Invoke(() => _viewModel.Refresh(snapshot, _clock.UtcNow));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Keep the refresh loop alive across transient failures.
            }
        }
    }
}
