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

    /// <summary>
    /// 单次连接的读取/写入期限默认值（S-PIPE-D1）。
    ///
    /// 本服务器以 maxNumberOfServerInstances:1 串行监听：同一时刻只接受一个连接，处理完才
    /// 回到 WaitForConnection。此前读取没有任何期限，因此本机任意一个进程只要连上管道后
    /// 不发送结束符（甚至只连不发），就能把监听循环永久挂死——次实例激活与任务计划程序的
    /// 外部触发转发会全部静默失效，而这对一个定时关机工具是可用性问题。
    ///
    /// 期限只覆盖「对端可控」的两段 I/O（读请求、写响应），不覆盖 TRIGGER 的业务处理：
    /// 触发要经 tasks.json 读取与调度引擎裁决，耗时由本地逻辑决定，不应被连接期限中断。
    /// 期限到期即关闭该连接并继续监听（fail-closed，绝不因单个连接停止服务）。
    /// 与 RemoteServer 的首字节 / 握手 / 整行读取期限同一思路。
    /// </summary>
    private static readonly TimeSpan DefaultConnectionIoDeadline = TimeSpan.FromSeconds(5);

    private readonly IWindowActivationService _windowActivation;
    private readonly ExternalTaskTriggerService? _triggerService;
    private readonly string _pipeName;
    private readonly TimeSpan _connectionIoDeadline;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();

    private Task? _listenTask;
    private bool _started;

    public ActivationPipeServer(
        IWindowActivationService windowActivation,
        ExternalTaskTriggerService? triggerService = null,
        string? pipeName = null,
        TimeSpan? connectionIoDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(windowActivation);
        _windowActivation = windowActivation;
        _triggerService = triggerService;
        // S-STARTUP-D1：允许测试注入独立管道名做聚焦回归（避免与真实运行实例的命名管道竞争）；
        // 生产路径保持默认协议名不变。
        _pipeName = pipeName ?? PipeName;
        // S-PIPE-D1：允许测试注入极短期限，确定性复现「连上不发结束符」的挂死场景。
        _connectionIoDeadline = connectionIoDeadline ?? DefaultConnectionIoDeadline;
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
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                string? line;
                try
                {
                    line = await ReadLineWithDeadlineAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 读取期限到期（对端连上后未在期限内发完一行）：关闭本连接，继续监听下一个。
                    continue;
                }

                string response;
                if (string.Equals(line, ActivateCommand, StringComparison.Ordinal))
                {
                    // S-STARTUP-D1-D1 启动未就绪协议：先快速应答「已接收」，激活在 UI 就绪后执行；
                    // 绝不在此同步等待被主实例启动步骤阻塞的 UI 线程（否则次实例会
                    // ActivationForwardFailed / 永久卡住）。主实例启动完成前有界确认，启动完成后再激活。
                    _windowActivation.ActivateMainWindowDeferred();
                    response = OkResponse;
                }
                else if (line is not null && line.StartsWith(TriggerCommandPrefix, StringComparison.Ordinal))
                {
                    // 业务处理不受连接期限约束：只传入服务停止令牌。
                    response = await HandleTriggerAsync(line, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    response = ErrorResponse;
                }

                try
                {
                    await WriteLineWithDeadlineAsync(pipe, response, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 写入期限到期（对端已不再读取）：关闭本连接，继续监听下一个。
                    continue;
                }
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

    /// <summary>
    /// 带期限的整行读取。期限到期抛 <see cref="TimeoutException"/>（与「服务停止」区分开：
    /// 服务停止时 cancellationToken 已取消，此时原样传播 OperationCanceledException 由外层 break）。
    /// </summary>
    private async Task<string?> ReadLineWithDeadlineAsync(
        PipeStream pipe,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_connectionIoDeadline);
        try
        {
            return await ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The activation pipe read deadline elapsed.");
        }
    }

    /// <summary>带期限的整行写入。期限到期抛 <see cref="TimeoutException"/>。</summary>
    private async Task WriteLineWithDeadlineAsync(
        PipeStream pipe,
        string text,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_connectionIoDeadline);
        try
        {
            await WriteLineAsync(pipe, text, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The activation pipe write deadline elapsed.");
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
