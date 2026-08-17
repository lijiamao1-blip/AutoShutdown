using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;

namespace AutoShutdown.App.Infrastructure.Remote;

/// <summary>
/// S23 局域网远程控制服务器（TCP）。每个连接处理一个请求后关闭（无状态、单请求）。
/// 硬性边界（与威胁模型一致）：
/// <list type="bullet">
/// <item>启动/连接时按 remote-settings.json 裁决：未启用/损坏/非法一律不监听（fail-closed）。</item>
/// <item>RequireTls=true 时拒绝任何明文连接（下行降级保护）；RequireTls=false 时明文连接仅
/// 以 IsTls=false 进入处理器 —— 配对与无人值守等效确认在处理器层仍强制 TLS。</item>
/// <item>请求体超过 <see cref="RemoteProtocol.MaxRequestBytes"/> 一律拒绝（InvalidPayload）。</item>
/// <item>任何传输/解析/握手异常直接关闭连接，绝不执行任何远程命令；决策只在
/// <see cref="RemoteRequestHandler"/>（走本地调度引擎唯一路径）。</item>
/// <item>本服务器不写任何配置/白名单/无人值守策略；日志/审计不含 PIN/secret/HMAC/私钥。</item>
/// </list>
/// 帧协议：客户端发送一行（以 '\n' 结尾）信封 JSON，服务端回一行 JSON-RPC 响应。请求/响应都不含换行。
/// </summary>
public sealed class RemoteServer : IRemoteServerControl, IDisposable
{
    /// <summary>TLS handshake record 的首字节（0x16），用于区分 TLS 与明文。</summary>
    private const byte SslHandshakeRecordByte = 0x16;

    private readonly RemoteSettingsStore _settingsStore;
    private readonly RemoteCertificateService _certificateService;
    private readonly RemoteRequestHandler _requestHandler;
    private readonly IClock _clock;
    private readonly IRemoteAuditLog _auditLog;
    private readonly IApplicationLogger _logger;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public RemoteServer(
        RemoteSettingsStore settingsStore,
        RemoteCertificateService certificateService,
        RemoteRequestHandler requestHandler,
        IClock clock,
        IRemoteAuditLog auditLog,
        IApplicationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(certificateService);
        ArgumentNullException.ThrowIfNull(requestHandler);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(logger);

        _settingsStore = settingsStore;
        _certificateService = certificateService;
        _requestHandler = requestHandler;
        _clock = clock;
        _auditLog = auditLog;
        _logger = logger;
    }

    /// <summary>
    /// 本地活动提示事件（CP5 高危提示）：连接（低危）与 triggerShutdown/cancelShutdown
    /// 成功派发（高危）时触发。绝不携带 PIN/secret/HMAC/私钥。
    /// </summary>
    public event EventHandler<RemoteServerNotification>? Notification;

    /// <summary>当前是否监听。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _listener is not null;
            }
        }
    }

    /// <summary>
    /// 按当前 remote-settings.json 启动监听。未启用/配置损坏/强制 TLS 时无法取得服务器证书，
    /// 一律不监听（fail-closed）。已在运行时重复调用为 no-op。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_listener is not null)
            {
                return;
            }
        }

        var settings = await LoadCurrentSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            _logger.Info("RemoteServer", "远程控制未启用或配置无效；不监听。");
            return;
        }

        if (settings.RequireTls)
        {
            try
            {
                _ = await _certificateService.GetServerCertificateAsync(settings, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error("RemoteServer", "无法取得 TLS 服务器证书，远程控制不启动。", exception);
                return;
            }
        }

        if (!IPAddress.TryParse(settings.ListenAddress, out var address))
        {
            _logger.Error("RemoteServer", "监听地址无效，远程控制不启动。");
            return;
        }

        TcpListener listener;
        try
        {
            listener = new TcpListener(address, settings.ListenPort);
            listener.Start();
        }
        catch (SocketException exception)
        {
            _logger.Error("RemoteServer", "无法监听指定端口，远程控制不启动。", exception);
            return;
        }

        lock (_sync)
        {
            if (_listener is not null)
            {
                listener.Stop();
                return;
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
        }

        var cts = _cts;
        _acceptLoop = AcceptLoopAsync(cts!.Token);
        _logger.Info(
            "RemoteServer",
            "远程控制已启动：" + settings.ListenAddress + ":" + settings.ListenPort
            + "，TLS=" + (settings.RequireTls ? "required" : "optional") + "。");
    }

    /// <summary>停止监听并关闭所有在途连接。</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        TcpListener? listener;
        Task? acceptLoop;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            listener = _listener;
            _listener = null;
            acceptLoop = _acceptLoop;
            _acceptLoop = null;
        }

        if (listener is not null)
        {
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
            }
        }

        if (cts is not null)
        {
            cts.Cancel();
        }

        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        cts?.Dispose();
        _logger.Info("RemoteServer", "远程控制已停止。");
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 退出路径不因停止失败而阻塞。
        }
    }

    // ===== 监听与连接 =====

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // 每个连接独立处理，异常内部吞掉（fail-closed 关闭连接）。
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                // 每次连接按当前设置裁决（白名单/开关/RequireTls 即时生效）。
                var settings = await LoadCurrentSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (settings is null)
                {
                    return;
                }

                var sourceIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

                using var network = client.GetStream();
                var firstByte = await ReadSingleByteAsync(network, cancellationToken).ConfigureAwait(false);
                if (firstByte < 0)
                {
                    return;
                }

                var isTls = firstByte == SslHandshakeRecordByte;
                using var pushback = new PushbackStream(network, (byte)firstByte);

                Stream transport;
                if (isTls)
                {
                    var certificate = await _certificateService
                        .GetServerCertificateAsync(settings, cancellationToken).ConfigureAwait(false);
                    var ssl = new SslStream(pushback, leaveInnerStreamOpen: true);
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            ClientCertificateRequired = settings.RequireClientCertificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AuthenticationException exception)
                    {
                        ssl.Dispose();
                        _auditLog.Write(TransportEntry(sourceIp, RemoteAuditOutcome.Unauthorized, "TLS handshake failed: " + exception.Message));
                        return;
                    }

                    transport = ssl;
                }
                else if (settings.RequireTls)
                {
                    // 强制 TLS：拒绝明文连接（下行降级保护）。
                    _auditLog.Write(TransportEntry(sourceIp, RemoteAuditOutcome.TlsBlocked, "Plaintext connection rejected: TLS is required."));
                    return;
                }
                else
                {
                    transport = pushback;
                }

                try
                {
                    var response = await HandleOneRequestAsync(
                        settings,
                        transport,
                        isTls: isTls,
                        sourceIp,
                        cancellationToken).ConfigureAwait(false);
                    if (response is not null)
                    {
                        await WriteResponseAsync(transport, response, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (ReferenceEquals(transport, pushback) == false)
                    {
                        transport.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 服务停止：静默关闭。
            }
            catch (Exception exception)
            {
                // fail-closed：任何传输/解析异常直接关闭连接，绝不执行远程命令。
                _logger.Warning("RemoteConnectionFailed", "远程连接异常关闭：" + exception.Message);
            }
        }
    }

    private async Task<RemoteResponse?> HandleOneRequestAsync(
        RemoteSettingsDocument settings,
        Stream transport,
        bool isTls,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var line = await ReadLineBytesAsync(transport, RemoteProtocol.MaxRequestBytes + 1, cancellationToken)
            .ConfigureAwait(false);
        if (line is null || line.Length == 0)
        {
            return null;
        }

        if (line.Length > RemoteProtocol.MaxRequestBytes)
        {
            _auditLog.Write(TransportEntry(sourceIp, RemoteAuditOutcome.Invalid, "Request exceeds the maximum size."));
            return ErrorResponse(null, RemoteErrorCode.InvalidPayload, "Request payload exceeds the maximum allowed size.");
        }

        var (envelope, parseCode) = ParseEnvelope(line);
        if (envelope is null)
        {
            _auditLog.Write(TransportEntry(sourceIp, RemoteAuditOutcome.Invalid, "Malformed request envelope."));
            return ErrorResponse(null, parseCode, "Malformed request envelope.");
        }

        var payload = ParsePayload(envelope.Payload);
        if (payload is null)
        {
            _auditLog.Write(TransportEntry(sourceIp, RemoteAuditOutcome.Invalid, "Malformed request payload."));
            return ErrorResponse(envelope.Id, RemoteErrorCode.InvalidRequest, "Malformed request payload.");
        }

        var context = new RemoteRequestContext
        {
            WhiteList = settings.WhiteList,
            IsTls = isTls,
            SourceIp = sourceIp,
            Envelope = envelope,
            Payload = payload
        };

        var response = await _requestHandler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
        RaiseNotification(payload.Method, sourceIp, response);
        return response;
    }

    /// <summary>
    /// 本地活动提示：任何成功处理到响应层的请求记为低危连接；triggerShutdown/cancelShutdown
    /// 仅在「被接受并派发」（响应无错误）时记高危提示。被拒绝/鉴权失败/白名单拦截不产生任何
    /// 电源动作，不作高危提示（避免把失败当成功提示）。通知绝不含任何敏感材料。
    /// </summary>
    private void RaiseNotification(string method, string sourceIp, RemoteResponse? response)
    {
        var handler = Notification;
        if (handler is null)
        {
            return;
        }

        var kind = method switch
        {
            RemoteProtocol.MethodTriggerShutdown => RemoteServerNotificationKind.TriggerShutdown,
            RemoteProtocol.MethodCancelShutdown => RemoteServerNotificationKind.CancelShutdown,
            _ => RemoteServerNotificationKind.Connection
        };

        // 高危方法只有成功派发才提示；成功与否以处理器实际响应为准（fail-closed：失败不误报）。
        if (kind is not RemoteServerNotificationKind.Connection && response?.Error is not null)
        {
            return;
        }

        handler(this, new RemoteServerNotification
        {
            Kind = kind,
            SourceIp = sourceIp,
            TimestampUtc = _clock.UtcNow
        });
    }

    private async Task<RemoteSettingsDocument?> LoadCurrentSettingsAsync(CancellationToken cancellationToken)
    {
        var load = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return load.Status == RemoteSettingsLoadStatus.Success && load.Document!.Enabled
            ? load.Document
            : null;
    }

    // ===== 信封/载荷解析 =====

    private static (RemoteRequestEnvelope? Envelope, RemoteErrorCode Code) ParseEnvelope(byte[] line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var envelope = document.RootElement.Deserialize<RemoteRequestEnvelope>();
            if (envelope is null)
            {
                return (null, RemoteErrorCode.InvalidRequest);
            }

            return (envelope, RemoteErrorCode.InvalidRequest);
        }
        catch (JsonException)
        {
            return (null, RemoteErrorCode.ParseError);
        }
    }

    private static RemotePayload? ParsePayload(string rawPayload)
    {
        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var payload = new RemotePayload
            {
                Version = root.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.Number
                    && version.TryGetInt32(out var parsedVersion)
                    ? parsedVersion
                    : 0,
                DeviceId = root.TryGetProperty("deviceId", out var deviceId)
                    ? deviceId.GetString() ?? string.Empty
                    : string.Empty,
                Timestamp = root.TryGetProperty("timestamp", out var timestamp)
                    && timestamp.ValueKind == JsonValueKind.Number
                    && timestamp.TryGetInt64(out var parsedTimestamp)
                    ? parsedTimestamp
                    : 0,
                Nonce = root.TryGetProperty("nonce", out var nonce)
                    ? nonce.GetString() ?? string.Empty
                    : string.Empty,
                Method = root.TryGetProperty("method", out var method)
                    ? method.GetString() ?? string.Empty
                    : string.Empty,
                Params = root.TryGetProperty("params", out var parameters) ? parameters.Clone() : default
            };
            return payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ===== 传输工具 =====

    private RemoteAuditEntry TransportEntry(string sourceIp, RemoteAuditOutcome outcome, string message) => new()
    {
        TimestampUtc = _clock.UtcNow,
        SourceIp = sourceIp,
        Method = string.Empty,
        Outcome = outcome,
        Message = message
    };

    private static RemoteResponse ErrorResponse(long? id, RemoteErrorCode code, string message) => new()
    {
        JsonRpc = RemoteProtocol.JsonRpcVersion,
        Id = id,
        Result = null,
        Error = new RemoteError
        {
            Code = (int)code,
            Message = message
        }
    };

    private static async Task WriteResponseAsync(
        Stream transport,
        RemoteResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(json);
        await transport.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await transport.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        await transport.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读一行（以 '\n' 结束），最多 maxBytes 字节；返回不含结尾换行的字节。EOF 且无内容返回 null。</summary>
    private static async Task<byte[]?> ReadLineBytesAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var newlineIndex = -1;
            for (var index = 0; index < read; index++)
            {
                if (chunk[index] == (byte)'\n')
                {
                    newlineIndex = index;
                    break;
                }
            }

            var appendCount = newlineIndex >= 0 ? newlineIndex : read;
            if (appendCount > 0)
            {
                buffer.Write(chunk, 0, appendCount);
            }

            if (newlineIndex >= 0)
            {
                break;
            }

            if (buffer.Length > maxBytes)
            {
                break;
            }
        }

        return buffer.ToArray();
    }

    private static async Task<int> ReadSingleByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var single = new byte[1];
        var read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : single[0];
    }

    /// <summary>把首字节塞回流的包装：服务端先读一个字节探测 TLS，再把该字节放回后续读取。</summary>
    private sealed class PushbackStream : Stream
    {
        private readonly Stream _inner;
        private byte[]? _prefix;

        public PushbackStream(Stream inner, byte firstByte)
        {
            _inner = inner;
            _prefix = [firstByte];
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefix is { Length: > 0 })
            {
                if (count == 0)
                {
                    return 0;
                }

                buffer[offset] = _prefix[0];
                _prefix = null;
                return 1;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix is { Length: > 0 })
            {
                if (buffer.Length == 0)
                {
                    return ValueTask.FromResult(0);
                }

                buffer.Span[0] = _prefix[0];
                _prefix = null;
                return ValueTask.FromResult(1);
            }

            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
