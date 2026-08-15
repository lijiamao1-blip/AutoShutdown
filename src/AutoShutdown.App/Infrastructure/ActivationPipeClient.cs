using System.IO.Pipes;
using System.Text;

namespace AutoShutdown.App.Infrastructure;

public static class ActivationPipeClient
{
    private const string PipeName = @"AutoShutdown.Desktop.Activation.v1";
    private const string ActivateCommand = "ACTIVATE";
    private const string OkResponse = "OK";
    private const int TotalTimeoutMilliseconds = 1500;
    private const int MaxMessageBytes = 64;

    public static async Task<bool> TryActivateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TotalTimeoutMilliseconds);

            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            var request = Encoding.UTF8.GetBytes(ActivateCommand + "\n");
            await pipe.WriteAsync(request, 0, request.Length, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            var response = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
            return string.Equals(response, OkResponse, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
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
}
