using System.Security.Cryptography;
using System.Text;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Remote;

public enum RemoteAuthStatus
{
    Unknown = 0,

    /// <summary>鉴权通过：返回解封后的共享密钥供上层使用。</summary>
    Succeeded = 1,

    /// <summary>HMAC 不符 / 未知设备 / 时间戳超窗 / nonce 重放。</summary>
    Unauthorized = 2,

    /// <summary>设备存储不可用（损坏/IO 失败，fail-closed）。</summary>
    Unavailable = 3
}

public sealed record RemoteAuthResult
{
    public RemoteAuthStatus Status { get; init; } = RemoteAuthStatus.Unknown;

    /// <summary>解封后的共享密钥（仅在 Succeeded 时有值）。</summary>
    public byte[]? Secret { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// 请求鉴权（S23 CP2）：对信封中原始 payload 字符串做 HMAC-SHA256 常量时间校验，并强制
/// 时间戳窗口（±5 分钟）与 nonce 防重放（TTL 缓存，有界）。设备不存在/存储损坏一律 fail-closed。
/// HMAC 正确性不依赖来源 IP；IP 仅用于审计。
/// </summary>
public sealed class RemoteAuthenticator
{
    private readonly PairingService _pairing;
    private readonly IClock _clock;

    // key = deviceId + '\n' + nonce，value = 过期 Unix 毫秒。普通字典 + 锁：保证「清理 → 容量检查 →
    // 添加」在并发下原子，未过期条目数绝不越过 _maxNonceEntries（远程拒绝服务边界）。
    private readonly Dictionary<string, long> _seenNonces = new();
    private readonly object _nonceGate = new();
    private readonly int _maxNonceEntries;

    public RemoteAuthenticator(
        PairingService pairing,
        IClock clock,
        int? nonceCacheMaxEntries = null)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(clock);
        _pairing = pairing;
        _clock = clock;
        _maxNonceEntries = nonceCacheMaxEntries ?? RemoteProtocol.NonceCacheMaxEntries;
        if (_maxNonceEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nonceCacheMaxEntries), "Nonce cache capacity must be at least 1.");
        }
    }

    public async Task<RemoteAuthResult> VerifyAsync(
        string rawPayload,
        string hmac,
        RemotePayload payload,
        CancellationToken cancellationToken)
    {
        var nowMs = _clock.UtcNow.ToUnixTimeMilliseconds();

        // 协议版本门：未知版本一律 fail-closed 拒绝。
        if (payload.Version != RemoteProtocol.ProtocolVersion)
        {
            return Unauthorized("Unsupported protocol version.");
        }

        // 时间戳窗口。
        if (Math.Abs(nowMs - payload.Timestamp) > RemoteProtocol.TimestampToleranceMs)
        {
            return Unauthorized("Request timestamp is outside the allowed window.");
        }

        if (string.IsNullOrWhiteSpace(payload.DeviceId))
        {
            return Unauthorized("Request deviceId is empty.");
        }

        if (string.IsNullOrWhiteSpace(payload.Nonce))
        {
            return Unauthorized("Request nonce is missing.");
        }

        // 先验证设备存在 + HMAC（fail-closed），再原子保留 nonce。非法/未知设备/HMAC 失败请求
        // 不得占用 nonce（防远程拒绝服务：未鉴权请求无法填满缓存）。
        var secret = await _pairing.GetDeviceSecretAsync(payload.DeviceId, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return Unauthorized("Device is not paired.");
        }

        if (!VerifyHmac(secret, rawPayload, hmac))
        {
            return Unauthorized("HMAC verification failed.");
        }

        // nonce 防重放 + 容量边界：清理过期项后未过期条目达到上限即拒绝新 nonce（绝不越限）；
        // 锁保证并发下不越界；TryAdd 保证同一 nonce 只允许一次（重放 fail-closed）。
        var nonceKey = payload.DeviceId + "\n" + payload.Nonce;
        lock (_nonceGate)
        {
            EvictExpired(nowMs);
            if (_seenNonces.Count >= _maxNonceEntries)
            {
                return Unauthorized("Nonce cache is at capacity.");
            }

            if (!_seenNonces.TryAdd(nonceKey, nowMs + RemoteProtocol.NonceTtlMs))
            {
                return Unauthorized("Request nonce was already used (replay).");
            }
        }

        return new RemoteAuthResult
        {
            Status = RemoteAuthStatus.Succeeded,
            Secret = secret,
            Reason = null
        };
    }

    private bool VerifyHmac(byte[] secret, string rawPayload, string providedHmac)
    {
        byte[] expected;
        try
        {
            using var hmac = new HMACSHA256(secret);
            expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawPayload));
        }
        catch (Exception)
        {
            return false;
        }

        // 常量时间比较（十六进制小写字符串）。
        var expectedHex = Convert.ToHexString(expected).ToLowerInvariant();
        var provided = (providedHmac ?? string.Empty).Trim();
        if (expectedHex.Length != provided.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHex),
            Encoding.ASCII.GetBytes(provided));
    }

    private void EvictExpired(long nowMs)
    {
        // 完整清理一遍过期条目，保证容量判定基于「未过期」条目数。调用方须持有 _nonceGate。
        List<string>? expired = null;
        foreach (var pair in _seenNonces)
        {
            if (pair.Value < nowMs)
            {
                (expired ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _seenNonces.Remove(key);
            }
        }
    }

    private static RemoteAuthResult Unauthorized(string reason) => new()
    {
        Status = RemoteAuthStatus.Unauthorized,
        Reason = reason
    };
}
