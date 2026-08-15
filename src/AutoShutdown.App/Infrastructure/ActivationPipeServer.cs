using System.IO;
using System.IO.Pipes;
using System.Text;

namespace AutoShutdown.App.Infrastructure;

public sealed class ActivationPipeServer : IAsyncDisposable
{
    public const string PipeName = @"AutoShutdown.Desktop.Activation.v1";

    private const string ActivateCommand = "ACTIVATE";
    private const string OkResponse = "OK";
    private const string ErrorResponse = "ERROR";
    private const int MaxMessageBytes = 64;

    private readonly IWindowActivationService _windowActivation;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();

    private Task? _listenTask;
    private bool _started;

    public ActivationPipeServer(IWindowActivationService windowActivation)
    {
        ArgumentNullException.ThrowIfNull(windowActivation);
        _windowActivation = windowActivation;
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
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? listenTask;
        lock (_sync)
        {
            listenTask = _listenTask;
        }

        _cts.Cancel();

        if (listenTask is not null)
        {
            try
            {
                await listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // The listen loop is best effort during shutdown.
            }
        }

        _cts.Dispose();
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var line = await ReadLineAsync(pipe, cancellationToken).ConfigureAwait(false);
                string response;
                if (string.Equals(line, ActivateCommand, StringComparison.Ordinal))
                {
                    _windowActivation.ActivateMainWindow();
                    response = OkResponse;
                }
                else
                {
                    response = ErrorResponse;
                }

                await WriteLineAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // Broken pipe: keep listening.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private static async Task<string?> ReadLineAsync(
        PipeStream pipe,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageBytes];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await pipe.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (value == (byte)'\n')
                {
                    return builder.ToString();
                }

                builder.Append((char)value);
            }

            if (builder.Length >= MaxMessageBytes)
            {
                return null;
            }
        }
    }

    private static async Task WriteLineAsync(
        PipeStream pipe,
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await pipe.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
