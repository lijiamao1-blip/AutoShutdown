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
/// S23-D1 远程拒绝服务边界测试（独立于原 S23 结果记录）。
/// 覆盖执行书检查点 5 的 7 项：
///   1) 非法/未知设备/HMAC 失败请求不占用 nonce；
///   2) 容量边界与并发：清理过期项后未过期条目达到上限时拒绝新 nonce，绝不越限（含真实 10,000 边界）；
///   3) 合法重放仍被拒绝（fail-closed）；
///   4) 慢首字节 → 期限关闭，无 handler/引擎/电源副作用；
///   5) 慢行帧 / 不完整帧 → 期限关闭，无副作用；
///   6) TLS 握手停滞 → 期限关闭，无副作用；
///   7) 并发上限 → 超限连接立即关闭，无副作用。
/// 鉴权侧用 Fake 时钟/保护器/审计；服务器侧真实套接字/TLS（本机回环），不触碰真实电源。
/// </summary>
public sealed class S23_D1_RemoteBoundaryTests
{
    private const string Pin = "123456";
    private const string ServerName = "TestPC";
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = BaseTime.ToUnixTimeMilliseconds();

    // ===== 鉴权边界：nonce 原子保留与容量上限 =====

    [Fact]
    public async Task Auth_BadHmacAndUnknownDevice_DoNotConsumeNonce()
    {
        // 容量仅 2：若非法/未知设备请求错误占用 nonce，后续合法请求会撞上「已满」。
        using var harness = new PairingHarness(nonceCacheMaxEntries: 2);
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseMs;

        // 未知设备（任意 HMAC）：拒绝，且不占 nonce。
        var (unknownRaw, unknownPayload) = harness.BuildRequest("unknown-device", timestamp, "nonce-unk");
        var unknown = await harness.Auth.VerifyAsync(unknownRaw, "ffffffffffffffff", unknownPayload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, unknown.Status);

        // 已配对设备但 HMAC 失败：拒绝，且不占 nonce。
        var (badRaw, badPayload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-bad");
        var bad = await harness.Auth.VerifyAsync(badRaw, "ffffffffffffffff", badPayload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, bad.Status);

        // 两个合法请求仍全部成功：证明上述失败没有消耗任何 nonce 空间。
        var (raw1, payload1) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-ok-1");
        var ok1 = await harness.Auth.VerifyAsync(raw1, Hmac(secret, raw1), payload1, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Succeeded, ok1.Status);

        var (raw2, payload2) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-ok-2");
        var ok2 = await harness.Auth.VerifyAsync(raw2, Hmac(secret, raw2), payload2, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Succeeded, ok2.Status);
    }

    [Fact]
    public async Task Auth_Capacity_RejectsNewNonceThenFreesAfterEviction()
    {
        using var harness = new PairingHarness(nonceCacheMaxEntries: 2);
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseMs;

        var (raw1, payload1) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-1");
        Assert.Equal(RemoteAuthStatus.Succeeded,
            (await harness.Auth.VerifyAsync(raw1, Hmac(secret, raw1), payload1, CancellationToken.None)).Status);

        var (raw2, payload2) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-2");
        Assert.Equal(RemoteAuthStatus.Succeeded,
            (await harness.Auth.VerifyAsync(raw2, Hmac(secret, raw2), payload2, CancellationToken.None)).Status);

        // 缓存已满（2/2）：清理过期项后仍未过期条目达到上限 → 拒绝新 nonce（绝不越限）。
        var (raw3, payload3) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-3");
        var full = await harness.Auth.VerifyAsync(raw3, Hmac(secret, raw3), payload3, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, full.Status);
        Assert.Equal("Nonce cache is at capacity.", full.Reason);

        // 时钟推进超过 TTL：下次请求先清理全部过期项，空间释放后新 nonce 可入。
        harness.Clock.UtcNow = BaseTime.AddMilliseconds(RemoteProtocol.NonceTtlMs + 1);
        var now2 = harness.Clock.UtcNow.ToUnixTimeMilliseconds();
        var (raw4, payload4) = harness.BuildRequest(paired.DeviceId!, now2, "nonce-4");
        var afterEvict = await harness.Auth.VerifyAsync(raw4, Hmac(secret, raw4), payload4, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Succeeded, afterEvict.Status);
    }

    [Fact]
    public async Task Auth_Concurrent_DoesNotExceedCapacity()
    {
        // 容量 50：300 个并发合法请求，锁序列化「清理 → 容量检查 → 添加」，
        // 恰好前 50 个入缓存，其余全部在容量边界被拒绝，缓存绝不越过 50。
        using var harness = new PairingHarness(nonceCacheMaxEntries: 50);
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseMs;

        var tasks = Enumerable.Range(0, 300).Select(async i =>
        {
            var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-" + i);
            var auth = await harness.Auth.VerifyAsync(raw, Hmac(secret, raw), payload, CancellationToken.None);
            return auth.Status;
        }).ToArray();
        var statuses = await Task.WhenAll(tasks);

        Assert.Equal(50, statuses.Count(status => status == RemoteAuthStatus.Succeeded));
        Assert.Equal(250, statuses.Count(status => status == RemoteAuthStatus.Unauthorized));
        Assert.Contains(statuses, status => status == RemoteAuthStatus.Unauthorized);
    }

    [Fact]
    public async Task Auth_ValidReplay_StillRejected()
    {
        using var harness = new PairingHarness(nonceCacheMaxEntries: 2);
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseMs;

        var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-same");
        var hmac = Hmac(secret, raw);

        var first = await harness.Auth.VerifyAsync(raw, hmac, payload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Succeeded, first.Status);

        // 同 nonce + 同时间戳重发 → 重放 fail-closed。
        var second = await harness.Auth.VerifyAsync(raw, hmac, payload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, second.Status);
        Assert.Equal("Request nonce was already used (replay).", second.Reason);
    }

    [Fact]
    public async Task Auth_RealCapacity_10000_RejectsAtBoundary()
    {
        using var harness = new PairingHarness(); // 默认容量 = RemoteProtocol.NonceCacheMaxEntries = 10000
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseMs;

        for (var i = 0; i < RemoteProtocol.NonceCacheMaxEntries; i++)
        {
            var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-" + i);
            var auth = await harness.Auth.VerifyAsync(raw, Hmac(secret, raw), payload, CancellationToken.None);
            Assert.Equal(RemoteAuthStatus.Succeeded, auth.Status);
        }

        // 第 10001 个：清理后仍满 → 拒绝，缓存绝不越限。
        var (rawLast, payloadLast) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-last");
        var rejected = await harness.Auth.VerifyAsync(rawLast, Hmac(secret, rawLast), payloadLast, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, rejected.Status);
        Assert.Equal("Nonce cache is at capacity.", rejected.Reason);
    }

    // ===== 服务器边界：连接期限与在途连接上限 =====

    [Fact]
    public async Task Server_SlowFirstByte_ClosesWithinDeadline_NoHandlerNoEngine()
    {
        using var harness = new ServerHarness(firstByteTimeout: TimeSpan.FromMilliseconds(300));
        var settings = EnabledSettings(requireTls: false);
        harness.SeedSettings(settings);
        await harness.Server.StartAsync(CancellationToken.None);
        Assert.True(harness.Server.IsRunning);

        await using (var conn = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
        {
            // 连接后不发任何字节：首字节在期限内未送达 → 服务端关闭连接。
            Assert.True(await ReadClosedAsync(conn), "慢首字节连接应在期限后关闭。");
        }

        await WaitUntilAsync(() => harness.Audit.Any(e => e.Message.Contains("Connection timed out")));
        Assert.Empty(harness.Engine.Submitted);
        Assert.DoesNotContain(harness.Audit, e => !string.IsNullOrEmpty(e.Method));
    }

    [Fact]
    public async Task Server_SlowLineAndIncompleteFrame_ClosesWithinDeadline_NoHandlerNoEngine()
    {
        using var harness = new ServerHarness(lineReadTimeout: TimeSpan.FromMilliseconds(300));
        var settings = EnabledSettings(requireTls: false);
        harness.SeedSettings(settings);
        await harness.Server.StartAsync(CancellationToken.None);

        await using (var conn = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
        {
            // 只发半个帧（无 '\n'），随后停滞：整行请求在期限内未收满 → 超时关闭。
            await conn.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"payload\":\"");
            Assert.True(await ReadClosedAsync(conn), "慢行帧/不完整帧连接应在期限后关闭。");
        }

        await WaitUntilAsync(() => harness.Audit.Any(e => e.Message.Contains("Connection timed out")));
        Assert.Empty(harness.Engine.Submitted);
        Assert.DoesNotContain(harness.Audit, e => !string.IsNullOrEmpty(e.Method));
    }

    [Fact]
    public async Task Server_TlsHandshakeStall_ClosesWithinDeadline_NoHandlerNoEngine()
    {
        using var harness = new ServerHarness(tlsHandshakeTimeout: TimeSpan.FromMilliseconds(300));
        var settings = EnabledSettings(requireTls: true);
        harness.SeedSettings(settings);
        await harness.Server.StartAsync(CancellationToken.None);

        await using (var conn = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
        {
            // 仅发送 TLS handshake record 首字节（0x16）后停滞：服务端握手在期限内未完成 → 关闭。
            await conn.SendRawAsync("\x16");
            Assert.True(await ReadClosedAsync(conn), "TLS 握手停滞连接应在期限后关闭。");
        }

        await WaitUntilAsync(() => harness.Audit.Any(e => e.Message.Contains("Connection timed out")));
        Assert.Empty(harness.Engine.Submitted);
        Assert.DoesNotContain(harness.Audit, e => !string.IsNullOrEmpty(e.Method));
    }

    [Fact]
    public async Task Server_ConcurrentConnections_AtLimit_NewConnectionClosed_NoHandlerNoEngine()
    {
        using var harness = new ServerHarness(
            firstByteTimeout: TimeSpan.FromMilliseconds(500),
            maxConcurrentConnections: 1);
        var settings = EnabledSettings(requireTls: false);
        harness.SeedSettings(settings);
        await harness.Server.StartAsync(CancellationToken.None);

        await using (var first = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
        {
            // 第一个连接占用唯一在途槽位（不发首字节，在期限内停顿）。
            await Task.Delay(150); // 让 accept 循环完成第一个连接的接入与槽位占用。

            await using (var second = await ConnectPlainAsync("127.0.0.1", settings.ListenPort))
            {
                // 达到并发上限：第二个连接被立即关闭（fail-closed，无响应）。
                Assert.True(await ReadClosedAsync(second), "超限连接应立即被关闭。");
            }

            // 占位的第一个连接也在期限内被关闭。
            Assert.True(await ReadClosedAsync(first), "占位连接也应在期限内关闭。");
        }

        await WaitUntilAsync(() =>
            harness.Audit.Any(e => e.Message.Contains("Connection rejected: in-flight connection limit reached.")));
        Assert.Empty(harness.Engine.Submitted);
        Assert.DoesNotContain(harness.Audit, e => !string.IsNullOrEmpty(e.Method));
    }

    // ===== 工具与测试替身 =====

    private static RemoteSettingsDocument EnabledSettings(bool requireTls) => new()
    {
        Enabled = true,
        ListenAddress = "127.0.0.1",
        ListenPort = GetFreePort(),
        RequireTls = requireTls,
        UseImportedCertificate = false
    };

    private static string Hmac(byte[] secret, string raw)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
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

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// 断言服务端已关闭连接：读到 EOF（空串）或 RST（IOException）都等价于「服务端关闭且无响应」。
    /// 3 秒兜底上限远大于被测期限（300ms/500ms）。
    /// </summary>
    private static async Task<bool> ReadClosedAsync(ClientConnection conn)
    {
        try
        {
            var line = await conn.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            return string.IsNullOrEmpty(line);
        }
        catch (IOException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
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

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autoshutdown-s23-d1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>鉴权侧夹具：注入可配置 nonce 容量，验证容量边界与并发不越限。</summary>
    private sealed class PairingHarness : IDisposable
    {
        public CapturingStorage Storage { get; }
        public FakeClock Clock { get; }
        public PairingService Pairing { get; }
        public RemoteAuthenticator Auth { get; }
        public List<RemoteAuditEntry> Audit { get; }

        public PairingHarness(CapturingStorage? storage = null, FakeClock? clock = null, int? nonceCacheMaxEntries = null)
        {
            Storage = storage ?? new CapturingStorage();
            Clock = clock ?? new FakeClock(BaseTime);
            Audit = [];
            Pairing = new PairingService(
                new RemoteDevicesStore(Storage),
                new RemotePairingLockStore(Storage),
                new FakeSecretProtector(),
                Clock,
                new FakeAuditLog(Audit),
                pinGenerator: () => Pin,
                secretGenerator: () => Enumerable.Range(0, RemoteProtocol.SharedSecretBytes).Select(i => (byte)(i + 1)).ToArray());
            Auth = new RemoteAuthenticator(Pairing, Clock, nonceCacheMaxEntries);
        }

        public void RotatePin() => Pairing.RotatePin();

        public (string Raw, RemotePayload Payload) BuildRequest(string deviceId, long timestamp, string nonce)
        {
            var raw =
                "{\"version\":1,\"deviceId\":\"" + deviceId
                + "\",\"timestamp\":" + timestamp
                + ",\"nonce\":\"" + nonce
                + "\",\"method\":\"queryStatus\",\"params\":{}}";
            using var doc = JsonDocument.Parse(raw);
            var payload = new RemotePayload
            {
                Version = 1,
                DeviceId = deviceId,
                Timestamp = timestamp,
                Nonce = nonce,
                Method = "queryStatus",
                Params = doc.RootElement.GetProperty("params")
            };
            return (raw, payload);
        }

        public void Dispose() { }
    }

    /// <summary>服务器侧夹具：注入可配置连接期限与在途连接上限，验证 fail-closed 无副作用。</summary>
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

        public ServerHarness(
            TimeSpan? firstByteTimeout = null,
            TimeSpan? tlsHandshakeTimeout = null,
            TimeSpan? lineReadTimeout = null,
            int? maxConcurrentConnections = null)
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "autoshutdown-s23-d1-server", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            Storage = new CapturingStorage();
            Clock = new FakeClock(BaseTime);
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
                Logger,
                firstByteTimeout,
                tlsHandshakeTimeout,
                lineReadTimeout,
                maxConcurrentConnections);
        }

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

    /// <summary>IStorage 包装：写入时把序列化结果种回内存，并可模拟损坏读。</summary>
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
