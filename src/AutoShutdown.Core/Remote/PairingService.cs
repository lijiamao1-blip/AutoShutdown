using System.Security.Cryptography;
using System.Text;
using AutoShutdown.Core.Abstractions;

namespace AutoShutdown.Core.Remote;

public enum PairingStatus
{
    Unknown = 0,

    /// <summary>配对成功：返回 deviceId 与 sharedSecret（仅 TLS 连接上交付）。</summary>
    Succeeded = 1,

    /// <summary>PIN 错误或不存在/过期：不配对。</summary>
    PinRejected = 2,

    /// <summary>连续失败达到阈值，配对已锁定（本阶段拒绝，仅本地 UI 可解锁）。</summary>
    Locked = 3,

    /// <summary>配对请求无法处理（设备/锁定存储损坏或 IO 失败，fail-closed）。</summary>
    Unavailable = 4
}

public sealed record PairingResult
{
    public PairingStatus Status { get; init; } = PairingStatus.Unknown;

    /// <summary>成功时的 deviceId。</summary>
    public string? DeviceId { get; init; }

    /// <summary>成功时的 sharedSecret（明文仅此一处交付，调用方只应经 TLS 回传）。</summary>
    public string? SharedSecret { get; init; }

    /// <summary>锁定剩余时间（Locked 时有值）。</summary>
    public TimeSpan? LockRemaining { get; init; }

    /// <summary>距锁定阈值还差的失败次数（PinRejected 时有值）。</summary>
    public int? RemainingAttempts { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed record RemotePinState
{
    /// <summary>当前有效 PIN（本地 UI 展示）。null = 无有效 PIN。</summary>
    public string? Pin { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

public sealed record RemotePairingLockState
{
    public bool IsLocked { get; init; }

    public TimeSpan? LockRemaining { get; init; }

    public int FailedAttempts { get; init; }
}

/// <summary>
/// 配对服务（S23 CP2）：PIN 生成/校验、连续失败限速与持久锁定（5 次 → 15 分钟，仅本地 UI 解锁）、
/// sharedSecret 生成与安全保存（经 <see cref="ISecretProtector"/> 保护后落盘，绝不明文）。
/// 锁定/存储损坏一律 fail-closed（拒绝配对）。远程端只能调用 <see cref="AttemptPairAsync"/>；
/// PIN 生成、解锁、设备清单只供本地 UI 使用。
/// </summary>
public sealed class PairingService
{
    private readonly RemoteDevicesStore _devicesStore;
    private readonly RemotePairingLockStore _lockStore;
    private readonly ISecretProtector _secretProtector;
    private readonly IClock _clock;
    private readonly IRemoteAuditLog _auditLog;
    private readonly Func<string> _pinGenerator;
    private readonly Func<byte[]> _secretGenerator;

    private readonly object _sync = new();
    private string? _activePin;
    private DateTimeOffset? _pinExpiresAtUtc;

    public PairingService(
        RemoteDevicesStore devicesStore,
        RemotePairingLockStore lockStore,
        ISecretProtector secretProtector,
        IClock clock,
        IRemoteAuditLog auditLog,
        Func<string>? pinGenerator = null,
        Func<byte[]>? secretGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(devicesStore);
        ArgumentNullException.ThrowIfNull(lockStore);
        ArgumentNullException.ThrowIfNull(secretProtector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(auditLog);

        _devicesStore = devicesStore;
        _lockStore = lockStore;
        _secretProtector = secretProtector;
        _clock = clock;
        _auditLog = auditLog;
        _pinGenerator = pinGenerator ?? GeneratePinValue;
        _secretGenerator = secretGenerator ?? GenerateSecretValue;
    }

    /// <summary>当前有效 PIN（本地 UI 展示）。线程安全快照。</summary>
    public RemotePinState CurrentPin
    {
        get
        {
            lock (_sync)
            {
                return new RemotePinState
                {
                    Pin = _activePin,
                    ExpiresAtUtc = _pinExpiresAtUtc
                };
            }
        }
    }

    /// <summary>本地 UI：生成新 PIN（覆盖旧 PIN）。</summary>
    public RemotePinState RotatePin()
    {
        lock (_sync)
        {
            _activePin = _pinGenerator();
            _pinExpiresAtUtc = _clock.UtcNow.Add(RemoteProtocol.PairingPinLifetime);
            return new RemotePinState
            {
                Pin = _activePin,
                ExpiresAtUtc = _pinExpiresAtUtc
            };
        }
    }

    /// <summary>本地 UI：当前锁定状态（不修改任何状态）。</summary>
    public async Task<RemotePairingLockState> GetLockStateAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var load = await _lockStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        switch (load.Status)
        {
            case RemotePairingLockLoadStatus.Success:
            case RemotePairingLockLoadStatus.NotFound:
                var lockedUntil = load.Document?.LockedUntilUtc;
                if (lockedUntil is { } until && until > now)
                {
                    return new RemotePairingLockState
                    {
                        IsLocked = true,
                        LockRemaining = until - now,
                        FailedAttempts = load.Document?.FailedAttempts ?? 0
                    };
                }

                return new RemotePairingLockState
                {
                    IsLocked = false,
                    FailedAttempts = load.Document?.FailedAttempts ?? 0
                };
            default:
                // 锁定存储损坏/IO 失败：fail-closed 视为锁定（拒绝配对），仅本地 UI 可解锁。
                return new RemotePairingLockState
                {
                    IsLocked = true,
                    FailedAttempts = RemoteProtocol.PairingMaxFailedAttempts
                };
        }
    }

    /// <summary>本地 UI：解锁（清除锁定与失败计数）。这是唯一解锁途径。</summary>
    public async Task<bool> UnlockAsync(CancellationToken cancellationToken)
    {
        var save = await _lockStore.SaveAsync(
            new RemotePairingLockDocument
            {
                SchemaVersion = RemotePairingLockDocument.CurrentSchemaVersion,
                FailedAttempts = 0,
                LockedUntilUtc = null
            },
            cancellationToken).ConfigureAwait(false);

        return save.Succeeded;
    }

    /// <summary>已配对设备清单（本地 UI；不含明文 secret）。</summary>
    public async Task<IReadOnlyList<PairedDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var load = await _devicesStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return load.Status is RemoteDevicesLoadStatus.Success or RemoteDevicesLoadStatus.NotFound
            ? load.Document?.Devices ?? []
            : [];
    }

    /// <summary>本地 UI：移除已配对设备（返回是否成功）。</summary>
    public async Task<bool> RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        var load = await _devicesStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is not (RemoteDevicesLoadStatus.Success or RemoteDevicesLoadStatus.NotFound))
        {
            return false;
        }

        var devices = (load.Document?.Devices ?? []).Where(device => device.DeviceId != deviceId).ToArray();
        var save = await _devicesStore.SaveAsync(
            new RemoteDevicesDocument
            {
                SchemaVersion = RemoteDevicesDocument.CurrentSchemaVersion,
                Devices = devices
            },
            cancellationToken).ConfigureAwait(false);

        return save.Succeeded;
    }

    /// <summary>远程端配对尝试。锁定中/存储损坏/无有效 PIN/PIN 错误一律 fail-closed。</summary>
    public async Task<PairingResult> AttemptPairAsync(
        string deviceName,
        string pin,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        // 1) 锁定检查（先于 PIN 校验）。
        var lockState = await GetLockStateAsync(cancellationToken).ConfigureAwait(false);
        if (lockState.IsLocked)
        {
            _auditLog.Write(new RemoteAuditEntry
            {
                TimestampUtc = _clock.UtcNow,
                SourceIp = sourceIp,
                Method = RemoteProtocol.MethodPair,
                Outcome = RemoteAuditOutcome.PairingLocked,
                Message = "Pairing rejected: lock active."
            });
            return new PairingResult
            {
                Status = PairingStatus.Locked,
                LockRemaining = lockState.LockRemaining,
                Message = "Pairing is locked; only the local user can unlock."
            };
        }

        // 2) 本地 UI 必须已生成有效 PIN。
        string? activePin;
        DateTimeOffset? pinExpiry;
        lock (_sync)
        {
            activePin = _activePin;
            pinExpiry = _pinExpiresAtUtc;
        }

        if (string.IsNullOrEmpty(activePin) || (pinExpiry is not null && pinExpiry.Value <= _clock.UtcNow))
        {
            await RecordFailureAsync(sourceIp, cancellationToken).ConfigureAwait(false);
            return new PairingResult
            {
                Status = PairingStatus.PinRejected,
                Message = "No valid pairing PIN is active."
            };
        }

        // 3) 常量时间 PIN 校验。
        if (!FixedTimeEquals(activePin, pin ?? string.Empty))
        {
            await RecordFailureAsync(sourceIp, cancellationToken).ConfigureAwait(false);
            var remaining = Math.Max(0, RemoteProtocol.PairingMaxFailedAttempts - lockState.FailedAttempts - 1);
            return new PairingResult
            {
                Status = PairingStatus.PinRejected,
                RemainingAttempts = remaining,
                Message = "Incorrect pairing PIN."
            };
        }

        // 4) PIN 正确：生成 secret、保护、登记设备、清零锁定。
        var secret = _secretGenerator();
        byte[] protectedSecret;
        try
        {
            protectedSecret = _secretProtector.Protect(secret);
        }
        catch (Exception)
        {
            _auditLog.Write(new RemoteAuditEntry
            {
                TimestampUtc = _clock.UtcNow,
                SourceIp = sourceIp,
                Method = RemoteProtocol.MethodPair,
                Outcome = RemoteAuditOutcome.Error,
                Message = "Pairing failed: secret protection failed."
            });
            return new PairingResult
            {
                Status = PairingStatus.Unavailable,
                Message = "Secret protection is unavailable."
            };
        }

        var deviceId = Guid.NewGuid().ToString("D");
        var now = _clock.UtcNow;

        var devicesLoad = await _devicesStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (devicesLoad.Status is not (RemoteDevicesLoadStatus.Success or RemoteDevicesLoadStatus.NotFound))
        {
            _auditLog.Write(new RemoteAuditEntry
            {
                TimestampUtc = now,
                SourceIp = sourceIp,
                Method = RemoteProtocol.MethodPair,
                Outcome = RemoteAuditOutcome.Error,
                Message = "Pairing failed: device store unavailable."
            });
            return new PairingResult
            {
                Status = PairingStatus.Unavailable,
                Message = "The paired-device store is unavailable."
            };
        }

        var existing = devicesLoad.Document?.Devices ?? [];
        var updated = new RemoteDevicesDocument
        {
            SchemaVersion = RemoteDevicesDocument.CurrentSchemaVersion,
            Devices =
            [
                .. existing,
                new PairedDevice
                {
                    DeviceId = deviceId,
                    DeviceName = (deviceName ?? string.Empty).Trim(),
                    ProtectedSecretBase64 = Convert.ToBase64String(protectedSecret),
                    PairedAtUtc = now,
                    LastSeenAtUtc = now
                }
            ]
        };

        var save = await _devicesStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        if (!save.Succeeded)
        {
            _auditLog.Write(new RemoteAuditEntry
            {
                TimestampUtc = now,
                SourceIp = sourceIp,
                Method = RemoteProtocol.MethodPair,
                Outcome = RemoteAuditOutcome.Error,
                Message = "Pairing failed: device store write failed."
            });
            return new PairingResult
            {
                Status = PairingStatus.Unavailable,
                Message = "The paired-device store could not be updated."
            };
        }

        // 5) 配对成功：清空锁定、消费 PIN（一 PIN 一配，防复用）、登记审计。
        await _lockStore.SaveAsync(
            new RemotePairingLockDocument
            {
                SchemaVersion = RemotePairingLockDocument.CurrentSchemaVersion,
                FailedAttempts = 0,
                LockedUntilUtc = null
            },
            cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _activePin = null;
            _pinExpiresAtUtc = null;
        }

        _auditLog.Write(new RemoteAuditEntry
        {
            TimestampUtc = now,
            DeviceId = deviceId,
            SourceIp = sourceIp,
            Method = RemoteProtocol.MethodPair,
            Outcome = RemoteAuditOutcome.PairingSucceeded,
            Message = "Pairing succeeded."
        });

        return new PairingResult
        {
            Status = PairingStatus.Succeeded,
            DeviceId = deviceId,
            SharedSecret = Convert.ToBase64String(secret),
            Message = "Pairing succeeded."
        };
    }

    /// <summary>读取某设备受保护的 secret 并解封（鉴权用）。设备不存在或解封失败返回 null（fail-closed）。</summary>
    internal async Task<byte[]?> GetDeviceSecretAsync(string deviceId, CancellationToken cancellationToken)
    {
        var load = await _devicesStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is not (RemoteDevicesLoadStatus.Success or RemoteDevicesLoadStatus.NotFound))
        {
            return null;
        }

        var device = (load.Document?.Devices ?? []).FirstOrDefault(candidate => candidate.DeviceId == deviceId);
        if (device is null || string.IsNullOrEmpty(device.ProtectedSecretBase64))
        {
            return null;
        }

        try
        {
            return _secretProtector.Unprotect(Convert.FromBase64String(device.ProtectedSecretBase64));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task RecordFailureAsync(string sourceIp, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var load = await _lockStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var current = load.Document?.FailedAttempts ?? 0;
        var attempts = current + 1;

        RemotePairingLockDocument next;
        if (attempts >= RemoteProtocol.PairingMaxFailedAttempts)
        {
            next = new RemotePairingLockDocument
            {
                SchemaVersion = RemotePairingLockDocument.CurrentSchemaVersion,
                FailedAttempts = attempts,
                LockedUntilUtc = now.Add(RemoteProtocol.PairingLockDuration)
            };
        }
        else
        {
            next = new RemotePairingLockDocument
            {
                SchemaVersion = RemotePairingLockDocument.CurrentSchemaVersion,
                FailedAttempts = attempts,
                LockedUntilUtc = null
            };
        }

        await _lockStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);

        _auditLog.Write(new RemoteAuditEntry
        {
            TimestampUtc = now,
            SourceIp = sourceIp,
            Method = RemoteProtocol.MethodPair,
            Outcome = RemoteAuditOutcome.PairingFailed,
            Message = next.LockedUntilUtc is not null
                ? "Pairing PIN rejected; lock engaged."
                : "Pairing PIN rejected."
        });
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string GeneratePinValue()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes) % 1_000_000;
        return value.ToString("D6");
    }

    private static byte[] GenerateSecretValue()
    {
        return RandomNumberGenerator.GetBytes(RemoteProtocol.SharedSecretBytes);
    }
}
