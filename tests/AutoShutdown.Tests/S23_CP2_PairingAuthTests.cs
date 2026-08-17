using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;
using AutoShutdown.Core.Storage;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>S23 CP2：配对与鉴权。PIN 限速/持久锁定/本地解锁、secret 安全保存（不写明文）、
/// 常量时间 HMAC 校验、时间戳窗口、nonce 防重放、存储损坏 fail-closed。
/// 全程 Fake 时钟/保护器/审计，不触碰真实 DPAPI。</summary>
public sealed class S23_CP2_PairingAuthTests
{
    private const string Pin = "123456";
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pairing_Success_ReturnsDeviceAndSecret_AndConsumesPin()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();

        var result = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);

        Assert.Equal(PairingStatus.Succeeded, result.Status);
        Assert.False(string.IsNullOrEmpty(result.DeviceId));
        Assert.False(string.IsNullOrEmpty(result.SharedSecret));

        // PIN 被消费：同一 PIN 不能再次配对。
        var second = await harness.Pairing.AttemptPairAsync("PC-C", Pin, "192.168.1.6", CancellationToken.None);
        Assert.Equal(PairingStatus.PinRejected, second.Status);

        // 设备已登记。
        var devices = await harness.Pairing.GetDevicesAsync(CancellationToken.None);
        Assert.Single(devices);
        Assert.Equal(result.DeviceId, devices[0].DeviceId);
        Assert.Equal("PC-B", devices[0].DeviceName);
    }

    [Fact]
    public async Task Pairing_Secret_IsStoredProtectedNotPlaintext()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();

        var result = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        Assert.Equal(PairingStatus.Succeeded, result.Status);

        // 落盘内容必须是保护器封装形态（Fake 保护器加 "PROT:" 前缀），绝不含原始明文 secret。
        var written = harness.Storage.WrittenJson(RemoteDevicesStore.FileName);
        Assert.Single(written);
        var document = JsonSerializer.Deserialize<RemoteDevicesDocument>(written[0]);
        var protectedB64 = Assert.Single(document!.Devices).ProtectedSecretBase64;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(protectedB64));
        Assert.Equal("PROT:" + result.SharedSecret, decoded);
    }

    [Fact]
    public async Task Pairing_FiveFailures_Locks_ThenLocalUnlockResets()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();

        for (var i = 0; i < 5; i++)
        {
            var rejected = await harness.Pairing.AttemptPairAsync("PC-B", "000000", "192.168.1.5", CancellationToken.None);
            Assert.Equal(PairingStatus.PinRejected, rejected.Status);
        }

        // 第 5 次失败后锁定：后续尝试直接 Locked。
        var locked = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        Assert.Equal(PairingStatus.Locked, locked.Status);
        Assert.NotNull(locked.LockRemaining);

        // 锁定跨重启持久：新建服务（同一存储）仍锁定。
        using var harness2 = new PairingHarness(harness.Storage, harness.Clock);
        var stillLocked = await harness2.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        Assert.Equal(PairingStatus.Locked, stillLocked.Status);

        // 本地 UI 解锁后恢复。
        Assert.True(await harness2.Pairing.UnlockAsync(CancellationToken.None));
        harness2.RotatePin();
        var ok = await harness2.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        Assert.Equal(PairingStatus.Succeeded, ok.Status);
    }

    [Fact]
    public async Task Pairing_NoActivePin_IsRejected()
    {
        using var harness = new PairingHarness();
        // 未生成 PIN。

        var result = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);

        Assert.Equal(PairingStatus.PinRejected, result.Status);
    }

    [Fact]
    public async Task Pairing_CorruptDeviceStore_FailsClosed()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        harness.Storage.SimulateRead(RemoteDevicesStore.FileName, StorageReadStatus.Corrupt);

        var result = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);

        Assert.Equal(PairingStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Pairing_CorruptLockStore_FailsClosedAsLocked()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        harness.Storage.SimulateRead(RemotePairingLockStore.FileName, StorageReadStatus.Corrupt);

        var result = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);

        Assert.Equal(PairingStatus.Locked, result.Status);
    }

    [Fact]
    public async Task Pairing_LockExpiry_AllowsRetry()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        for (var i = 0; i < 5; i++)
        {
            await harness.Pairing.AttemptPairAsync("PC-B", "000000", "192.168.1.5", CancellationToken.None);
        }

        var locked = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        Assert.Equal(PairingStatus.Locked, locked.Status);

        // 15 分钟后锁到期；此时旧 PIN 也已过期，须由本地 UI 重新生成 PIN。
        harness.Clock.UtcNow = BaseTime.Add(RemoteProtocol.PairingLockDuration).AddSeconds(1);
        harness.RotatePin();
        var ok = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        Assert.Equal(PairingStatus.Succeeded, ok.Status);
    }

    // ===== 鉴权 =====

    [Fact]
    public async Task Auth_HmacCorrect_TimeInWindow_NewNonce_Succeeds()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseTime.ToUnixTimeMilliseconds();

        var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-1");
        var auth = await harness.Auth.VerifyAsync(raw, Hmac(secret, raw), payload, CancellationToken.None);

        Assert.Equal(RemoteAuthStatus.Succeeded, auth.Status);
    }

    [Fact]
    public async Task Auth_WrongHmac_Unauthorized()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var timestamp = BaseTime.ToUnixTimeMilliseconds();

        var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-1");
        var auth = await harness.Auth.VerifyAsync(raw, "ffffffffffffffff", payload, CancellationToken.None);

        Assert.Equal(RemoteAuthStatus.Unauthorized, auth.Status);
    }

    [Theory]
    [InlineData(-6 * 60 * 1000)]
    [InlineData(6 * 60 * 1000)]
    public async Task Auth_TimestampOutOfWindow_Unauthorized(long offsetMs)
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseTime.AddMilliseconds(offsetMs).ToUnixTimeMilliseconds();

        var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-1");
        var auth = await harness.Auth.VerifyAsync(raw, Hmac(secret, raw), payload, CancellationToken.None);

        Assert.Equal(RemoteAuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task Auth_NonceReplay_Unauthorized()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseTime.ToUnixTimeMilliseconds();

        var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-same");
        var hmac = Hmac(secret, raw);

        var first = await harness.Auth.VerifyAsync(raw, hmac, payload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Succeeded, first.Status);

        var second = await harness.Auth.VerifyAsync(raw, hmac, payload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, second.Status);
    }

    [Fact]
    public async Task Auth_UnknownDevice_Unauthorized()
    {
        using var harness = new PairingHarness();
        var timestamp = BaseTime.ToUnixTimeMilliseconds();
        var (raw, payload) = harness.BuildRequest("unknown-device", timestamp, "nonce-1");

        var auth = await harness.Auth.VerifyAsync(raw, "abc", payload, CancellationToken.None);

        Assert.Equal(RemoteAuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task Auth_CorruptDeviceStore_UnauthorizedFailClosed()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var timestamp = BaseTime.ToUnixTimeMilliseconds();
        var (raw, payload) = harness.BuildRequest(paired.DeviceId!, timestamp, "nonce-1");

        harness.Storage.SimulateRead(RemoteDevicesStore.FileName, StorageReadStatus.Corrupt);

        var auth = await harness.Auth.VerifyAsync(raw, "abc", payload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task Auth_EmptyDeviceId_Unauthorized()
    {
        using var harness = new PairingHarness();
        var timestamp = BaseTime.ToUnixTimeMilliseconds();

        using var doc = JsonDocument.Parse(
            "{\"version\":1,\"deviceId\":\"\",\"timestamp\":" + timestamp + ",\"nonce\":\"n\",\"method\":\"queryStatus\",\"params\":{}}");
        var payload = new RemotePayload
        {
            Version = 1,
            DeviceId = string.Empty,
            Timestamp = timestamp,
            Nonce = "n",
            Method = "queryStatus",
            Params = doc.RootElement.GetProperty("params")
        };

        var auth = await harness.Auth.VerifyAsync(string.Empty, "abc", payload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task Auth_UnsupportedVersion_Unauthorized()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        var paired = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        var secret = Convert.FromBase64String(paired.SharedSecret!);
        var timestamp = BaseTime.ToUnixTimeMilliseconds();

        var raw =
            "{\"version\":99,\"deviceId\":\"" + paired.DeviceId
            + "\",\"timestamp\":" + timestamp
            + ",\"nonce\":\"nonce-v2\",\"method\":\"queryStatus\",\"params\":{}}";
        using var doc = JsonDocument.Parse(raw);
        var payload = new RemotePayload
        {
            Version = 99,
            DeviceId = paired.DeviceId!,
            Timestamp = timestamp,
            Nonce = "nonce-v2",
            Method = "queryStatus",
            Params = doc.RootElement.GetProperty("params")
        };

        var auth = await harness.Auth.VerifyAsync(raw, Hmac(secret, raw), payload, CancellationToken.None);
        Assert.Equal(RemoteAuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task Pairing_LockState_ReportsRemaining()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();
        for (var i = 0; i < 5; i++)
        {
            await harness.Pairing.AttemptPairAsync("PC-B", "000000", "192.168.1.5", CancellationToken.None);
        }

        var state = await harness.Pairing.GetLockStateAsync(CancellationToken.None);

        Assert.True(state.IsLocked);
        Assert.NotNull(state.LockRemaining);
        Assert.True(state.LockRemaining > TimeSpan.Zero);
        Assert.Equal(5, state.FailedAttempts);
    }

    [Fact]
    public async Task Pairing_SuccessfulPair_ResetsLockCounter()
    {
        using var harness = new PairingHarness();
        harness.RotatePin();

        // 3 次失败（未到 5 次阈值）。
        for (var i = 0; i < 3; i++)
        {
            await harness.Pairing.AttemptPairAsync("PC-B", "000000", "192.168.1.5", CancellationToken.None);
        }

        var before = await harness.Pairing.GetLockStateAsync(CancellationToken.None);
        Assert.Equal(3, before.FailedAttempts);
        Assert.False(before.IsLocked);

        // 正确 PIN 配对成功 → 失败计数清零。
        harness.RotatePin();
        var ok = await harness.Pairing.AttemptPairAsync("PC-B", Pin, "192.168.1.5", CancellationToken.None);
        Assert.Equal(PairingStatus.Succeeded, ok.Status);

        var after = await harness.Pairing.GetLockStateAsync(CancellationToken.None);
        Assert.Equal(0, after.FailedAttempts);
        Assert.False(after.IsLocked);
    }

    // ===== 存储 fail-closed 路径 =====

    [Fact]
    public async Task Store_Devices_NotFound_ThenInvalid_ThenUnsupported_Classifications()
    {
        var storage = new CapturingStorage();
        var store = new RemoteDevicesStore(storage);

        Assert.Equal(RemoteDevicesLoadStatus.NotFound, (await store.LoadAsync(CancellationToken.None)).Status);

        storage.Seed(RemoteDevicesStore.FileName, "[]"); // 根不是对象 → Invalid
        Assert.Equal(RemoteDevicesLoadStatus.Invalid, (await store.LoadAsync(CancellationToken.None)).Status);

        storage.Seed(RemoteDevicesStore.FileName, "{\"SchemaVersion\":99}"); // 未来版本 → UnsupportedVersion
        Assert.Equal(RemoteDevicesLoadStatus.UnsupportedVersion, (await store.LoadAsync(CancellationToken.None)).Status);

        // 无 ProtectedSecretBase64 的设备 → Invalid。
        storage.Seed(RemoteDevicesStore.FileName,
            "{\"SchemaVersion\":1,\"Devices\":[{\"DeviceId\":\"x\",\"ProtectedSecretBase64\":\"\"}]}");
        Assert.Equal(RemoteDevicesLoadStatus.Invalid, (await store.LoadAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Store_PairingLock_Classifications()
    {
        var storage = new CapturingStorage();
        var store = new RemotePairingLockStore(storage);

        Assert.Equal(RemotePairingLockLoadStatus.NotFound, (await store.LoadAsync(CancellationToken.None)).Status);

        storage.SimulateRead(RemotePairingLockStore.FileName, StorageReadStatus.Corrupt);
        Assert.Equal(RemotePairingLockLoadStatus.Corrupt, (await store.LoadAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Store_Settings_NotFound_ThenInvalid_ThenUnsupported_Classifications()
    {
        var storage = new CapturingStorage();
        var store = new RemoteSettingsStore(storage);

        Assert.Equal(RemoteSettingsLoadStatus.NotFound, (await store.LoadAsync(CancellationToken.None)).Status);

        storage.Seed(RemoteSettingsStore.FileName, "[]");
        Assert.Equal(RemoteSettingsLoadStatus.Invalid, (await store.LoadAsync(CancellationToken.None)).Status);

        storage.Seed(RemoteSettingsStore.FileName, "{\"SchemaVersion\":99}");
        Assert.Equal(RemoteSettingsLoadStatus.UnsupportedVersion, (await store.LoadAsync(CancellationToken.None)).Status);

        // 非法端口 → Invalid（绝不回退为「启用」）。
        storage.Seed(RemoteSettingsStore.FileName,
            "{\"SchemaVersion\":1,\"Enabled\":true,\"ListenPort\":70000,\"ListenAddress\":\"127.0.0.1\"}");
        Assert.Equal(RemoteSettingsLoadStatus.Invalid, (await store.LoadAsync(CancellationToken.None)).Status);

        storage.SimulateRead(RemoteSettingsStore.FileName, StorageReadStatus.Corrupt);
        Assert.Equal(RemoteSettingsLoadStatus.Corrupt, (await store.LoadAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Store_Settings_DefaultIsDisabled()
    {
        var storage = new CapturingStorage();
        var store = new RemoteSettingsStore(storage);

        // 无文件 → 视为未启用远程（绝不监听），同时允许配置新值。
        var load = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(RemoteSettingsLoadStatus.NotFound, load.Status);
        Assert.Null(load.Document);

        var save = await store.SaveAsync(
            new RemoteSettingsDocument
            {
                Enabled = true,
                ListenAddress = "127.0.0.1",
                ListenPort = 48620
            },
            CancellationToken.None);
        Assert.True(save.Succeeded);

        var reload = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(RemoteSettingsLoadStatus.Success, reload.Status);
        Assert.True(reload.Document!.Enabled);
    }

    // ===== 测试替身 =====

    private sealed class PairingHarness : IDisposable
    {
        public CapturingStorage Storage { get; }
        public FakeClock Clock { get; }
        public PairingService Pairing { get; }
        public RemoteAuthenticator Auth { get; }
        public List<RemoteAuditEntry> Audit { get; }

        public PairingHarness(CapturingStorage? storage = null, FakeClock? clock = null)
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
            Auth = new RemoteAuthenticator(Pairing, Clock);
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

    private static string Hmac(byte[] secret, string raw)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>IStorage 包装：捕获每次序列化写入内容（校验「secret 受保护落盘」），
    /// 并允许把指定路径模拟为指定读状态（测试损坏 fail-closed）。</summary>
    private sealed class CapturingStorage : IStorage
    {
        private readonly InMemoryStorage _inner = new();
        private readonly List<(string Path, string Json)> _writes = new();
        private readonly Dictionary<string, StorageReadStatus> _simulatedReads = new();

        public IReadOnlyList<string> WrittenJson(string path)
            => _writes.Where(entry => entry.Path == path).Select(entry => entry.Json).ToList();

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
            var json = JsonSerializer.Serialize(value);
            _writes.Add((relativePath, json));
            // InMemoryStorage 只记录路径不回写内容；这里把序列化结果种回内存，保证「写入后可读」。
            _inner.Seed(relativePath, json);
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
        public void Write(RemoteAuditEntry entry) => sink.Add(entry);
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }
}
