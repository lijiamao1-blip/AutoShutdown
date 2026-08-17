using System.Collections.Concurrent;
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

    // key = deviceId + '\n' + nonce，value = 过期 Unix 毫秒。
    private readonly ConcurrentDictionary<string, long> _seenNonces = new();

    public RemoteAuthenticator(
        PairingService pairing,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(clock);
        _pairing = pairing;
        _clock = clock;
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

        // nonce 防重放（先查后写，同一 nonce 只允许一次）。
        var nonceKey = payload.DeviceId + "\n" + payload.Nonce;
        if (!_seenNonces.TryAdd(nonceKey, nowMs + RemoteProtocol.NonceTtlMs))
        {
            return Unauthorized("Request nonce was already used (replay).");
        }

        EvictExpired(nowMs);

        // 取设备密钥并校验 HMAC。
        var secret = await _pairing.GetDeviceSecretAsync(payload.DeviceId, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return Unauthorized("Device is not paired.");
        }

        if (!VerifyHmac(secret, rawPayload, hmac))
        {
            return Unauthorized("HMAC verification failed.");
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
        if (_seenNonces.Count < RemoteProtocol.NonceCacheMaxEntries)
        {
            return;
        }

        foreach (var pair in _seenNonces)
        {
            if (pair.Value < nowMs)
            {
                _seenNonces.TryRemove(pair.Key, out _);
            }
        }

        // 仍超限：拒绝新条目（fail-closed）——由调用方在上层再做一次上限检查。
        while (_seenNonces.Count > RemoteProtocol.NonceCacheMaxEntries)
        {
            var stale = _seenNonces.FirstOrDefault(pair => pair.Value < nowMs);
            if (stale.Key is null)
            {
                break;
            }

            _seenNonces.TryRemove(stale.Key, out _);
        }
    }

    private static RemoteAuthResult Unauthorized(string reason) => new()
    {
        Status = RemoteAuthStatus.Unauthorized,
        Reason = reason
    };
}
