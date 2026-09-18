namespace AutoShutdown.Core.Remote;

/// <summary>
/// S23 局域网远程控制协议常量与威胁模型冻结项。
///
/// 威胁模型（S23 CP1）：
/// <list type="bullet">
/// <item>不依赖来源 IP 鉴权；IP 仅用于审计。</item>
/// <item>每次签名请求携带 deviceId + timestamp + nonce + HMAC-SHA256；服务端常量时间校验、
/// 时间窗与 nonce 防重放。</item>
/// <item>配对 PIN 连续 5 次失败锁定 15 分钟；锁定期间拒绝配对，仅本地 UI 可解锁。</item>
/// <item>远程触发 + 无人值守等效确认必须 TLS；无 TLS 不得等效确认，只能回退本地 Countdown。</item>
/// <item>配对必须 TLS：sharedSecret 只在 TLS 连接上交付，绝不明文落盘或日志。</item>
/// <item>任何远程请求不得修改本地配置、无人值守策略或任何白名单。</item>
/// </list>
/// 本类只声明常量；行为由 PairingService / RemoteAuthenticator / RemoteRequestHandler 实现。
/// </summary>
public static class RemoteProtocol
{
    /// <summary>JSON-RPC 固定版本串。</summary>
    public const string JsonRpcVersion = "2.0";

    /// <summary>S23 协议版本（版本协商）。不匹配一律拒绝（UnsupportedVersion）。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>单条请求原始字节上限（消息大小）。超过一律 ParseError/InvalidRequest。</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>时间戳容差（毫秒）。|now - timestamp| 超过即拒绝（Unauthorized）。</summary>
    public const long TimestampToleranceMs = 5 * 60 * 1000;

    /// <summary>
    /// nonce 防重放缓存 TTL（毫秒）。必须覆盖「同一条请求仍可能被接受」的完整区间，
    /// 否则 nonce 会在请求仍处于有效时间窗内被清理，打开重放窗口。
    ///
    /// 推导：时间戳判据是 |now - timestamp| ≤ TimestampToleranceMs，即同时容忍落后与超前
    /// 各 TimestampToleranceMs。设一条请求携带的时间戳为 T、首次到达时刻为 A：
    /// 客户端时钟超前时 T 最大可达 A + TimestampToleranceMs，而该请求的时间戳窗口一直
    /// 延续到 T + TimestampToleranceMs = A + 2 × TimestampToleranceMs。
    /// 若 TTL 只取 1 × TimestampToleranceMs，nonce 在 A + TimestampToleranceMs 即被清理，
    /// 而原请求在此后仍有最长 TimestampToleranceMs 的时间窗可通过校验——抓包重放一次即可成功。
    /// 因此 TTL 取 2 × TimestampToleranceMs，保证 nonce 的存活期完整覆盖时间戳有效期。
    ///
    /// 注意：TTL 加倍会使 nonce 在缓存中的驻留时间加倍，容量压力相应上升；
    /// <see cref="NonceCacheMaxEntries"/> 按此已留有余量（10000 条 / 10 分钟 ≈ 16 请求每秒）。
    /// </summary>
    public const long NonceTtlMs = 2 * TimestampToleranceMs;

    /// <summary>nonce 防重放缓存最大条目数（到达后先清理过期项，仍满则拒绝新条目，绝不越限）。</summary>
    public const int NonceCacheMaxEntries = 10000;

    /// <summary>连接首字节读取期限（远程拒绝服务防护：慢首字节在此期限内未送达即关闭连接）。</summary>
    public static readonly TimeSpan ConnectionFirstByteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>TLS 握手期限（握手停滞在此期限内未完成即关闭连接）。</summary>
    public static readonly TimeSpan ConnectionTlsHandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>整行请求读取期限（慢行帧 / 不完整帧在此期限内未收满一行即关闭连接）。</summary>
    public static readonly TimeSpan ConnectionLineReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>在途连接上限（达到上限时新连接立即关闭，fail-closed 无 handler/引擎副作用）。</summary>
    public const int MaxConcurrentConnections = 64;

    /// <summary>配对 PIN 连续失败锁定阈值。</summary>
    public const int PairingMaxFailedAttempts = 5;

    /// <summary>锁定持续时间。</summary>
    public static readonly TimeSpan PairingLockDuration = TimeSpan.FromMinutes(15);

    /// <summary>配对 PIN 有效期。</summary>
    public static readonly TimeSpan PairingPinLifetime = TimeSpan.FromMinutes(10);

    /// <summary>配对 PIN 长度。</summary>
    public const int PairingPinLength = 6;

    /// <summary>sharedSecret 长度（字节）。</summary>
    public const int SharedSecretBytes = 32;

    /// <summary>远程触发回退本地 Countdown 的默认告警窗口（秒）。</summary>
    public const int FallbackCountdownSeconds = 60;

    /// <summary>HMAC 输出形式：hex 小写。</summary>
    public const string HmacHexFormat = "x2";

    // ===== 方法名（冻结） =====

    public const string MethodPair = "pair";

    public const string MethodQueryStatus = "queryStatus";

    public const string MethodListTasks = "listTasks";

    public const string MethodTriggerShutdown = "triggerShutdown";

    public const string MethodCancelShutdown = "cancelShutdown";
}

/// <summary>JSON-RPC 错误码（S23 CP1 冻结）。</summary>
public enum RemoteErrorCode
{
    ParseError = -32700,

    InvalidRequest = -32600,

    MethodNotFound = -32601,

    InvalidParams = -32602,

    /// <summary>鉴权失败：HMAC 不符 / 时间戳超窗 / nonce 重放 / 未知 deviceId。</summary>
    Unauthorized = -32603,

    /// <summary>服务未启用 / 引擎未运行。</summary>
    ServerNotReady = 1,

    /// <summary>协议版本不匹配。</summary>
    UnsupportedVersion = 2,

    /// <summary>TLS 要求不满足（配对或等效确认需要 TLS）。</summary>
    TlsRequired = 3,

    /// <summary>配对失败（PIN 错误 / 已锁定 / 锁定中）。</summary>
    PairingFailed = 4,

    /// <summary>配对锁定中。</summary>
    PairingLocked = 5,

    /// <summary>远程命令白名单拒绝。</summary>
    Forbidden = 6,

    /// <summary>本地任务不存在 / 无对应实例可操作。</summary>
    TaskNotFound = 7,

    /// <summary>本地调度引擎或倒计时边界拒绝 / 异常。</summary>
    ExecutionRejected = 8,

    /// <summary>本地任务已禁用。</summary>
    TaskDisabled = 9,

    /// <summary>请求体过大 / 结构非法。</summary>
    InvalidPayload = 10
}
