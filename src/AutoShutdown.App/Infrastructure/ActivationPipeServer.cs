using System.IO;
using System.IO.Pipes;
using System.Text;
using AutoShutdown.Core.Scheduling.TaskSchedulerSync;

namespace AutoShutdown.App.Infrastructure;

public sealed class ActivationPipeServer : IAsyncDisposable
{
    public const string PipeName = @"AutoShutdown.Desktop.Activation.v1";

    private const string ActivateCommand = "ACTIVATE";
    private const string TriggerCommandPrefix = "TRIGGER ";
    private const string OkResponse = "OK";
    private const string ErrorResponse = "ERROR";
    private const int MaxMessageBytes = 64;

    private readonly IWindowActivationService _windowActivation;
    private readonly ExternalTaskTriggerService? _triggerService;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();

    private Task? _listenTask;
    private bool _started;

    public ActivationPipeServer(
        IWindowActivationService windowActivation,
        ExternalTaskTriggerService? triggerService = null)
    {
        ArgumentNullException.ThrowIfNull(windowActivation);
        _windowActivation = windowActivation;
        _triggerService = triggerService;
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
                else if (line is not null && line.StartsWith(TriggerCommandPrefix, StringComparison.Ordinal))
                {
                    response = await HandleTriggerAsync(line, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 处理外部触发转发（S22 CP4）：只把稳定本地 task id 交给触发服务裁决并交回本地唯一
    /// Workflow；本服务器绝不直接执行电源。outcome 只用于审计，响应 OK 表示已交付。
    /// </summary>
    private async Task<string> HandleTriggerAsync(
        string line,
        CancellationToken cancellationToken)
    {
        var idText = line[TriggerCommandPrefix.Length..];
        if (_triggerService is null
            || !Guid.TryParse(idText, out var taskId)
            || taskId == Guid.Empty)
        {
            return ErrorResponse;
        }

        try
        {
            await _triggerService
                .HandleExternalTriggerAsync(taskId, cancellationToken)
                .ConfigureAwait(false);
            return OkResponse;
        }
        catch (OperationCanceledException)
        {
            return ErrorResponse;
        }
        catch (Exception)
        {
            // 触发服务本身已 fail-closed；异常只意味着未交付。
            return ErrorResponse;
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
