using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Remote;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S23 CP3 应用层：TLS 服务器 + 证书（DPAPI 保护）+ DPAPI 保护器 + 文件审计日志。
/// 覆盖：TLS 握手成功端到端配对/查询、明文降级拒绝、强制 TLS 明文拒绝、不信任服务器证书拒绝握手、
/// 超长请求拒绝、未启用/损坏配置 fail-closed 不监听、自签名证书 DPAPI 保护与持久化、
/// 导入证书密码、过期证书 fail-closed、DPAPI 往返、审计日志无敏感字段。
/// 全程真实套接字/TLS（本机回环）；不触碰真实电源，不写任何配置/白名单/策略。
/// </summary>
public sealed class S23_CP3_RemoteServerTests
{
    private const string Pin = "123456";
    private const string ServerName = "TestPC";
    private static readonly DateTimeOffset Base = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();

    // ===== DpapiSecretProtector =====

    [Fact]
    public void DpapiProtector_RoundTrip_RestoresPlaintext()
    {
        var protector = new DpapiSecretProtector();
        byte[] plaintext = Enumerable.Range(0, RemoteProtocol.SharedSecretBytes).Select(i => (byte)i).ToArray();

        var protectedBytes = protector.Protect(plaintext);
        var restored = protector.Unprotect(protectedBytes);

        Assert.Equal(plaintext, restored);
    }

    [Fact]
    public void DpapiProtector_Garbage_Throws_FailClosed()
    {
        var protector = new DpapiSecretProtector();

        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(Encoding.UTF8.GetBytes("not-dpapi-data")));
    }

    // ===== FileRemoteAuditLog =====

    [Fact]
    public void AuditLog_NoSecretFields_OneEntryPerLine()
    {
        var dir = CreateTempDir();
        try
        {
            var log = new FileRemoteAuditLog(dir);
            log.Write(new RemoteAuditEntry
            {
                TimestampUtc = Base,
                DeviceId = "dev-1",
                SourceIp = "192.168.1.5",
                Method = RemoteProtocol.MethodQueryStatus,
                Outcome = RemoteAuditOutcome.Succeeded,
                Message = "queryStatus served."
            });
            log.Write(new RemoteAuditEntry
            {
                TimestampUtc = Base,
                SourceIp = "192.168.1.6",
                Method = RemoteProtocol.MethodPair,
                Outcome = RemoteAuditOutcome.PairingFailed,
                Message = "Pairing PIN rejected."
            });

            var file = Assert.Single(Directory.GetFiles(dir));
            var text = File.ReadAllText(file);
            Assert.Equal(2, text.TrimEnd('\n').Split('\n').Length);

            // 结构上绝不出现敏感字段名。
            foreach (var forbidden in new[] { "hmac", "sharedSecret", "\"secret\"", "\"pin\"", "privateKey" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("queryStatus", text);
            Assert.Contains("192.168.1.5", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ===== RemoteCertificateService =====

    [Fact]
    public async Task Certificate_SelfSigned_ProtectedOnDisk_AndPersists()
    {
        var dir = CreateTempDir();
        try
        {
            var certPath = Path.Combine(dir, "remote-server-cert.dpapi");
            var pwdPath = Path.Combine(dir, "pwd.dpapi");
            var clock = new FakeClock(Base);
            var settings = new RemoteSettingsDocument { UseImportedCertificate = false };

            var service1 = new RemoteCertificateService(new DpapiSecretProtector(), clock, certPath, pwdPath);
            var cert1 = await service1.GetServerCertificateAsync(settings, CancellationToken.None);

            Assert.True(cert1.HasPrivateKey);
            Assert.True(cert1.NotAfter > clock.UtcNow);

            // 落盘必须是 DPAPI 封装形态，绝非明文证书/PFX：无法直接按证书解析，且不含主体名字节。
            Assert.True(File.Exists(certPath));
            var onDisk = await File.ReadAllBytesAsync(certPath);
            Assert.ThrowsAny<CryptographicException>(() => new X509Certificate2(onDisk));
            Assert.False(ContainsBytes(onDisk, Encoding.UTF8.GetBytes("AutoShutdown Remote Server")));

            // 持久化：新实例从磁盘恢复同一证书。
            var service2 = new RemoteCertificateService(new DpapiSecretProtector(), clock, certPath, pwdPath);
            var cert2 = await service2.GetServerCertificateAsync(settings, CancellationToken.None);
            Assert.Equal(cert1.Thumbprint, cert2.Thumbprint);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Certificate_ImportedPfx_RequiresDpapiPassword_ThenLoads()
    {
        var dir = CreateTempDir();
        try
        {
            var certPath = Path.Combine(dir, "cert.dpapi");
            var pwdPath = Path.Combine(dir, "pwd.dpapi");
            var clock = new FakeClock(Base);

            byte[] pfx;
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=Imported Test",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                using var selfSigned = request.CreateSelfSigned(Base.AddDays(-1), Base.AddDays(365));
                pfx = selfSigned.Export(X509ContentType.Pfx, "import-pwd");
            }

            var importPath = Path.Combine(dir, "import.pfx");
            await File.WriteAllBytesAsync(importPath, pfx);

            var service = new RemoteCertificateService(new DpapiSecretProtector(), clock, certPath, pwdPath);
            var settings = new RemoteSettingsDocument
            {
                UseImportedCertificate = true,
                ImportedCertPath = importPath
            };

            // 带密码 PFX 且本地未提供密码：fail-closed 抛异常（不静默降级为无密码加载）。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetServerCertificateAsync(settings, CancellationToken.None));

            // 本地 UI 写入 DPAPI 保护的密码后成功加载。
            await File.WriteAllBytesAsync(
                pwdPath,
                new DpapiSecretProtector().Protect(Encoding.UTF8.GetBytes("import-pwd")));
            var cert = await service.GetServerCertificateAsync(settings, CancellationToken.None);
            Assert.True(cert.HasPrivateKey);
            Assert.Equal("CN=Imported Test", cert.Subject);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Certificate_Expired_FailClosed_Throws()
    {
        var dir = CreateTempDir();
        try
        {
            byte[] pfx;
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=Expired Test",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                using var selfSigned = request.CreateSelfSigned(Base.AddDays(-10), Base.AddDays(-5));
                pfx = selfSigned.Export(X509ContentType.Pfx, string.Empty);
            }

            var importPath = Path.Combine(dir, "expired.pfx");
            await File.WriteAllBytesAsync(importPath, pfx);

            var service = new RemoteCertificateService(
                new DpapiSecretProtector(),
                new FakeClock(Base),
                Path.Combine(dir, "cert.dpapi"),
                Path.Combine(dir, "pwd.dpapi"));
            var settings = new RemoteSettingsDocument
            {
                UseImportedCertificate = true,
                ImportedCertPath = importPath
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetServerCertificateAsync(settings, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ===== RemoteServer =====

    [Fact]
    public async Task Server_NotEnabled_DoesNotListen()
    {
        using var harness = new ServerHarness();
        harness.SeedSettings(new RemoteSettingsDocument { Enabled = false });

        await harness.Server.StartAsync(CancellationToken.None);

        Assert.False(harness.Server.IsRunning);
    }

    [Fact]
    public async Task Server_CorruptSettings_FailClosed_DoesNotListen()
    {
        using var harness = new ServerHarness();
        harness.Storage.SimulateRead(RemoteSettingsStore.FileName, StorageReadStatus.Corrupt);

        await harness.Server.StartAsync(CancellationToken.None);

        Assert.False(harness.Server.IsRunning);
    }

    [Fact]
    public async Task Server_TlsEndToEnd_PairThenQueryStatus()
    {
        using var harness = new ServerHarness();
        var settings = EnabledSettings(requireTls: true);
        harness.SeedSettings(settings);
        harness.RotatePin();
        await harness.Server.StartAsync(CancellationToken.None);
        Assert.True(harness.Server.IsRunning);

        // 配对（TLS）。
        await using (var pair = await ConnectTlsAsync("127.0.0.1", settings.ListenPort, trustServer: true))
        {
            var pairLine = BuildEnvelope(
                string.Empty, BaseMs, "nonce-pair", RemoteProtocol.MethodPair,
                "{\"deviceName\":\"PC-B\",\"pin\":\"" + Pin + "\"}", secret: null);
            await pair.SendLineAsync(pairLine);
            var pairResponse = ParseResponse(await pair.ReadLineAsync());
            Assert.Null(pairResponse.Error);
            var result = pairResponse.Result!.Value.Deserialize<RemotePairResult>()!;
            Assert.Equal(ServerName, result.ServerName);
            Assert.False(string.IsNullOrEmpty(result.DeviceId));
            Assert.False(string.IsNullOrEmpty(result.SharedSecret));

            // 鉴权查询（同一连接单请求后即关闭，此处走新连接）。
            await using (var query = await ConnectTlsAsync("127.0.0.1", settings.ListenPort, trustServer: true))
            {
                var secret = Convert.FromBase64String(result.SharedSecret);
                var queryLine = BuildEnvelope(
                    result.DeviceId, BaseMs, "nonce-q", RemoteProtocol.MethodQueryStatus, "{}", secret);
                await query.SendLineAsync(queryLine);
                var queryResponse = ParseResponse(await query.ReadLineAsync());
                Assert.Null(queryResponse.Error);
                var status = queryResponse.Result!.Value.Deserialize<RemoteStatusResult>()!;
                Assert.Equal(ServerName, status.ServerName);
                Assert.Equal("Running", status.EngineStatus);
            }
        }
    }

    [Fact]
    public async Task Server_Plaintext_WhenRequireTls_Rejected_FailClosed()
    {
        using var harness = new ServerHarness();
        var settings = EnabledSettings(requireTls: true);
        harness.SeedSettings(settings);
        await harness.Server.StartAsync(CancellationToken.None);

        await using (var conn = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
        {
            await conn.SendLineAsync(BuildEnvelope(
                string.Empty, BaseMs, "n", RemoteProtocol.MethodPair,
                "{\"deviceName\":\"PC-B\",\"pin\":\"123456\"}", secret: null));

            // 强制 TLS：明文连接被关闭，无任何响应。服务端关闭带未读数据时客户端会读到
            // RST（IOException）而非 EOF —— 两者都等价于「服务端拒绝且无响应」。
            string? line = null;
            try
            {
                line = await conn.ReadLineAsync();
            }
            catch (IOException)
            {
                line = null;
            }

            Assert.True(string.IsNullOrEmpty(line));
        }

        await WaitUntilAsync(() => harness.Audit.Any(e => e.Outcome == RemoteAuditOutcome.TlsBlocked));
    }

    [Fact]
    public async Task Server_Plaintext_WhenTlsOptional_PairRejected_TlsRequired()
    {
        using var harness = new ServerHarness();
        var settings = EnabledSettings(requireTls: false);
        harness.SeedSettings(settings);
        harness.RotatePin();
        await harness.Server.StartAsync(CancellationToken.None);

        await using (var conn = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
        {
            await conn.SendLineAsync(BuildEnvelope(
                string.Empty, BaseMs, "n", RemoteProtocol.MethodPair,
                "{\"deviceName\":\"PC-B\",\"pin\":\"" + Pin + "\"}", secret: null));
            var response = ParseResponse(await conn.ReadLineAsync());

            // 明文连接允许传输，但配对仍必须 TLS（sharedSecret 只在 TLS 上交付）。
            Assert.NotNull(response.Error);
            Assert.Equal((int)RemoteErrorCode.TlsRequired, response.Error!.Code);
        }
    }

    [Fact]
    public async Task Server_UntrustedClientCert_TlsHandshakeFails()
    {
        using var harness = new ServerHarness();
        var settings = EnabledSettings(requireTls: true);
        harness.SeedSettings(settings);
        await harness.Server.StartAsync(CancellationToken.None);

        // 客户端不信任服务器自签名证书 → 握手失败（AuthenticationException），请求不可能
        // 到达处理器。服务端关闭带未读数据时客户端也可能读到 RST（IOException）——两者等价。
        var exception = await Record.ExceptionAsync(() =>
            ConnectTlsAsync("127.0.0.1", settings.ListenPort, trustServer: false));
        Assert.NotNull(exception);
        Assert.True(
            exception is AuthenticationException or IOException,
            "握手必须失败，实际异常：" + exception!.GetType().Name);

        await WaitUntilAsync(() => harness.Engine.Submitted.Count == 0);
        Assert.Empty(harness.Engine.Submitted);
    }

    [Fact]
    public async Task Server_OversizedRequest_Rejected_NoEngineSubmit()
    {
        using var harness = new ServerHarness();
        var settings = EnabledSettings(requireTls: false);
        harness.SeedSettings(settings);
        await harness.Server.StartAsync(CancellationToken.None);

        var huge = new string('x', RemoteProtocol.MaxRequestBytes + 256);
        var oversized = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1L, payload = huge, hmac = string.Empty });

        await using (var conn = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
        {
            await conn.SendRawAsync(oversized + "\n");
            var response = ParseResponse(await conn.ReadLineAsync());

            Assert.NotNull(response.Error);
            Assert.Equal((int)RemoteErrorCode.InvalidPayload, response.Error!.Code);
            Assert.Empty(harness.Engine.Submitted);
        }
    }

    [Fact]
    public async Task Server_TriggerDefaultWhiteList_Forbidden_NoEngineSubmit()
    {
        using var harness = new ServerHarness();
        var settings = EnabledSettings(requireTls: true);
        harness.SeedSettings(settings);
        harness.RotatePin();
        await harness.Server.StartAsync(CancellationToken.None);

        var deviceId = string.Empty;
        var secret = Array.Empty<byte>();
        await using (var pair = await ConnectTlsAsync("127.0.0.1", settings.ListenPort, trustServer: true))
        {
            await pair.SendLineAsync(BuildEnvelope(
                string.Empty, BaseMs, "n-pair", RemoteProtocol.MethodPair,
                "{\"deviceName\":\"PC-B\",\"pin\":\"" + Pin + "\"}", secret: null));
            var pairResponse = ParseResponse(await pair.ReadLineAsync());
            var result = pairResponse.Result!.Value.Deserialize<RemotePairResult>()!;
            deviceId = result.DeviceId;
            secret = Convert.FromBase64String(result.SharedSecret);
        }

        await using (var conn = await ConnectTlsAsync("127.0.0.1", settings.ListenPort, trustServer: true))
        {
            // 默认白名单只读：triggerShutdown 必须本地显式启用，否则 Forbidden，绝不触及引擎/电源。
            await conn.SendLineAsync(BuildEnvelope(
                deviceId, BaseMs, "n-trigger", RemoteProtocol.MethodTriggerShutdown,
                "{\"taskId\":\"" + Guid.NewGuid().ToString("D") + "\"}", secret));
            var response = ParseResponse(await conn.ReadLineAsync());

            Assert.NotNull(response.Error);
            Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
            Assert.Empty(harness.Engine.Submitted);
        }
    }

    // ===== 夹具与客户端 =====

    private static RemoteSettingsDocument EnabledSettings(bool requireTls) => new()
    {
        Enabled = true,
        ListenAddress = "127.0.0.1",
        ListenPort = GetFreePort(),
        RequireTls = requireTls,
        UseImportedCertificate = false
    };

    private static string BuildEnvelope(
        string deviceId,
        long timestamp,
        string nonce,
        string method,
        string paramsJson,
        byte[]? secret)
    {
        var rawPayload = "{\"version\":1,\"deviceId\":\"" + deviceId
            + "\",\"timestamp\":" + timestamp
            + ",\"nonce\":\"" + nonce
            + "\",\"method\":\"" + method
            + "\",\"params\":" + paramsJson + "}";
        var hmac = secret is null ? string.Empty : Hmac(secret, rawPayload);
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1L, payload = rawPayload, hmac });
    }

    private static string Hmac(byte[] secret, string raw)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static RemoteResponse ParseResponse(string line)
        => JsonSerializer.Deserialize<RemoteResponse>(line)!;

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<ClientConnection> ConnectTlsAsync(string host, int port, bool trustServer)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port);
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => trustServer
            });
            return new ClientConnection(client, ssl);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<ClientConnection> ConnectPlainAsync(string host, int port)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port);
            return new ClientConnection(client, client.GetStream());
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autoshutdown-s23-cp3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matches = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for the condition.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ServerHarness : IDisposable
    {
        public CapturingStorage Storage { get; }
        public FakeClock Clock { get; }
        public List<RemoteAuditEntry> Audit { get; }
        public FakeLogger Logger { get; }
        public RemoteSettingsStore SettingsStore { get; }
        public PairingService Pairing { get; }
        public RemoteRequestHandler Handler { get; }
        public RemoteCertificateService CertificateService { get; }
        public RemoteServer Server { get; }
        public FakeSchedulerEngine Engine { get; }
        private readonly string _tempRoot;

        public ServerHarness()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "autoshutdown-s23-cp3-server", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            Storage = new CapturingStorage();
            Clock = new FakeClock(Base);
            Audit = [];
            Logger = new FakeLogger();
            Engine = new FakeSchedulerEngine();
            SettingsStore = new RemoteSettingsStore(Storage);
            var devicesStore = new RemoteDevicesStore(Storage);
            var lockStore = new RemotePairingLockStore(Storage);
            Pairing = new PairingService(
                devicesStore,
                lockStore,
                new FakeSecretProtector(),
                Clock,
                new FakeAuditLog(Audit),
                pinGenerator: () => Pin,
                secretGenerator: () => Enumerable.Range(0, RemoteProtocol.SharedSecretBytes)
                    .Select(i => (byte)(i + 1)).ToArray());
            var authenticator = new RemoteAuthenticator(Pairing, Clock);
            CertificateService = new RemoteCertificateService(
                new FakeSecretProtector(),
                Clock,
                Path.Combine(_tempRoot, "remote-server-cert.dpapi"),
                Path.Combine(_tempRoot, "remote-imported-cert-password.dpapi"));
            Handler = new RemoteRequestHandler(
                Pairing,
                authenticator,
                Engine,
                new FakeTaskService(),
                Clock,
                new FakeAuditLog(Audit),
                () => ServerName);
            Server = new RemoteServer(
                SettingsStore,
                CertificateService,
                Handler,
                Clock,
                new FakeAuditLog(Audit),
                Logger);
        }

        public void RotatePin() => Pairing.RotatePin();

        public void SeedSettings(RemoteSettingsDocument settings)
            => Storage.Seed(RemoteSettingsStore.FileName, JsonSerializer.Serialize(settings));

        public void Dispose()
        {
            Server.Dispose();
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } =
            new() { EngineStatus = SchedulerEngineStatus.Running };

        public List<SchedulerCommand> Submitted { get; } = new();

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Submitted.Add(command);
            return Task.FromResult(new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = Snapshot,
                Message = "ok"
            });
        }

        public SchedulerSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class FakeTaskService : ITaskService
    {
        private readonly Dictionary<Guid, TaskDefinition> _definitions = new();

        public event EventHandler<TaskCollectionChangedEventArgs>? CollectionChanged
        {
            add { }
            remove { }
        }

        public TaskDefinition? Get(Guid taskId) => _definitions.GetValueOrDefault(taskId);

        public IReadOnlyCollection<TaskDefinition> GetAll() => _definitions.Values.ToList();

        public TaskCommandResult Create(TaskDefinition definition, DateTimeOffset now, TimeZoneInfo timeZone)
            => throw new NotSupportedException();

        public TaskCommandResult Snooze(TaskInstance current, TimeSpan duration, DateTimeOffset now)
            => throw new NotSupportedException();

        public TaskCommandResult Cancel(TaskInstance current) => throw new NotSupportedException();

        public TaskCommandResult RescheduleAfterArbitration(TaskInstance current, TimeSpan delay, DateTimeOffset now)
            => throw new NotSupportedException();

        public TaskCommandResult RescheduleDaily(TaskDefinition definition, TaskInstance current, DateTimeOffset now, TimeZoneInfo timeZone)
            => throw new NotSupportedException();

        public TaskCommandResult RescheduleRecurring(TaskDefinition definition, TaskInstance current, DateTimeOffset now, TimeZoneInfo timeZone)
            => throw new NotSupportedException();

        public TaskCollectionResult Add(TaskDefinition definition) => throw new NotSupportedException();

        public TaskCollectionResult Update(TaskDefinition definition) => throw new NotSupportedException();

        public TaskCollectionResult Remove(Guid taskId) => throw new NotSupportedException();

        public TaskCollectionResult SetEnabled(Guid taskId, bool isEnabled) => throw new NotSupportedException();
    }

    /// <summary>IStorage 包装：写入时把序列化结果种回内存（保证「写入后可读」），并可模拟损坏读。</summary>
    private sealed class CapturingStorage : IStorage
    {
        private readonly InMemoryStorage _inner = new();
        private readonly Dictionary<string, StorageReadStatus> _simulatedReads = new();

        public void Seed(string path, string json) => _inner.Seed(path, json);

        public void SimulateRead(string path, StorageReadStatus status) => _simulatedReads[path] = status;

        public Task<StorageReadResult<T>> ReadAsync<T>(string relativePath, CancellationToken cancellationToken)
        {
            if (_simulatedReads.TryGetValue(relativePath, out var status))
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = status,
                    Error = "Simulated read status."
                });
            }

            return _inner.ReadAsync<T>(relativePath, cancellationToken);
        }

        public Task<StorageWriteResult> WriteAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
        {
            _inner.Seed(relativePath, JsonSerializer.Serialize(value));
            return _inner.WriteAsync(relativePath, value, cancellationToken);
        }
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext)
            => Encoding.UTF8.GetBytes("PROT:" + Convert.ToBase64String(plaintext));

        public byte[] Unprotect(byte[] protectedBytes)
        {
            var value = Encoding.UTF8.GetString(protectedBytes);
            if (!value.StartsWith("PROT:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Not a protected value.");
            }

            return Convert.FromBase64String(value["PROT:".Length..]);
        }
    }

    private sealed class FakeAuditLog(List<RemoteAuditEntry> sink) : IRemoteAuditLog
    {
        private readonly object _gate = new();

        public void Write(RemoteAuditEntry entry)
        {
            lock (_gate)
            {
                sink.Add(entry);
            }
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private sealed class FakeLogger : IApplicationLogger
    {
        public List<string> Messages { get; } = new();

        public string LogDirectory => string.Empty;

        public bool IsEnabled(ApplicationLogLevel level) => true;

        public void Log(ApplicationLogLevel level, string eventName, string message, Exception? exception = null)
        {
            Messages.Add(eventName + ": " + message);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;

        public ClientConnection(TcpClient client, Stream stream)
        {
            _client = client;
            _stream = stream;
        }

        public async Task SendLineAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _stream.WriteAsync(bytes).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        public async Task SendRawAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _stream.WriteAsync(bytes).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        public async Task<string> ReadLineAsync()
        {
            using var buffer = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                var read = await _stream.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (one[0] == (byte)'\n')
                {
                    break;
                }

                buffer.WriteByte(one[0]);
                if (buffer.Length > RemoteProtocol.MaxRequestBytes * 2)
                {
                    throw new InvalidOperationException("Response line exceeded the safety cap.");
                }
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _client.Dispose();
        }
    }
}
