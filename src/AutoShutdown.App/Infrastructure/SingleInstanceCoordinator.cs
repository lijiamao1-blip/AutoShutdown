namespace AutoShutdown.App.Infrastructure;

public enum SingleInstanceResult
{
    Unknown = 0,
    Primary = 1,
    Secondary = 2,
    Error = 3
}

public sealed record SingleInstanceAcquireResult
{
    public SingleInstanceResult Result { get; init; } = SingleInstanceResult.Unknown;

    public string Message { get; init; } = string.Empty;
}

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\AutoShutdown.Desktop.Singleton.v1";

    private Mutex? _mutex;
    private bool _owned;

    public SingleInstanceAcquireResult TryAcquirePrimary()
    {
        if (_mutex is not null)
        {
            return new SingleInstanceAcquireResult
            {
                Result = SingleInstanceResult.Error,
                Message = "Acquire has already been attempted by this coordinator."
            };
        }

        try
        {
            var mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                var owned = mutex.WaitOne(0);
                if (owned)
                {
                    _mutex = mutex;
                    _owned = true;
                    return new SingleInstanceAcquireResult
                    {
                        Result = SingleInstanceResult.Primary,
                        Message = "Primary instance acquired."
                    };
                }
            }
            catch (AbandonedMutexException)
            {
                _mutex = mutex;
                _owned = true;
                return new SingleInstanceAcquireResult
                {
                    Result = SingleInstanceResult.Primary,
                    Message = "Primary instance acquired after an abandoned mutex was recovered."
                };
            }

            mutex.Dispose();
            return new SingleInstanceAcquireResult
            {
                Result = SingleInstanceResult.Secondary,
                Message = "Another primary instance is already running."
            };
        }
        catch (Exception exception)
        {
            return new SingleInstanceAcquireResult
            {
                Result = SingleInstanceResult.Error,
                Message = "Failed to acquire the single instance mutex: " + exception.Message
            };
        }
    }

    public void Dispose()
    {
        if (_mutex is not null)
        {
            try
            {
                if (_owned)
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch (Exception)
            {
                // Best effort; the process is leaving anyway.
            }

            _mutex.Dispose();
            _mutex = null;
            _owned = false;
        }
    }
}
