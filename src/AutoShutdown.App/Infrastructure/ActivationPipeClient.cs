using System.IO.Pipes;
using System.Text;

namespace AutoShutdown.App.Infrastructure;

public static class ActivationPipeClient
{
    private const string PipeName = @"AutoShutdown.Desktop.Activation.v1";
    private const string ActivateCommand = "ACTIVATE";
    private const string TriggerCommandPrefix = "TRIGGER ";
    private const string OkResponse = "OK";
    private const int TotalTimeoutMilliseconds = 1500;
    private const int MaxMessageBytes = 64;

    public static Task<bool> TryActivateAsync(CancellationToken cancellationToken)
        => RequestAsync(ActivateCommand, cancellationToken);

    /// <summary>
    /// 把外部触发回调转发给已在运行的主实例（S22 CP4）：副实例只转发「稳定本地 task id」，
    /// 不执行任何电源；主实例经 ExternalTaskTriggerService 裁决并交回本地唯一 Workflow。
    /// </summary>
    public static Task<bool> TryTriggerAsync(Guid taskId, CancellationToken cancellationToken)
        => taskId == Guid.Empty
            ? Task.FromResult(false)
            : RequestAsync(TriggerCommandPrefix + taskId.ToString("D"), cancellationToken);

    private static async Task<bool> RequestAsync(string command, CancellationToken cancellationToken)
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

            var request = Encoding.UTF8.GetBytes(command + "\n");
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
