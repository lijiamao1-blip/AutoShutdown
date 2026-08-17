using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoShutdown.Core.Remote;

/// <summary>
/// S23 线上请求信封。客户端把整个 payload 以 JSON 字符串原样签名，服务端对收到的原始
/// payload 字符串做 HMAC 校验（避免 JSON 规范化歧义）。配对请求 hmac 为空串。
/// </summary>
public sealed record RemoteRequestEnvelope
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = RemoteProtocol.JsonRpcVersion;

    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>被签名的 JSON 字符串（原始字节参与 HMAC）。</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;

    /// <summary>base64(HMAC-SHA256(sharedSecret, UTF8(payload)))。配对请求为空串。</summary>
    [JsonPropertyName("hmac")]
    public string Hmac { get; init; } = string.Empty;
}

/// <summary>payload JSON 解析后的内容（version/method 冻结字段）。</summary>
public sealed record RemotePayload
{
    public int Version { get; init; }

    /// <summary>已配对设备 id；配对请求为空串。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Unix 毫秒时间戳。</summary>
    public long Timestamp { get; init; }

    /// <summary>请求 nonce（防重放）。</summary>
    public string Nonce { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    /// <summary>方法参数（JSON 对象），按方法解析。</summary>
    public JsonElement Params { get; init; }
}

/// <summary>JSON-RPC 响应。</summary>
public sealed record RemoteResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = RemoteProtocol.JsonRpcVersion;

    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public RemoteError? Error { get; init; }
}

/// <summary>JSON-RPC 错误体（不含任何 secret/PIN/HMAC）。</summary>
public sealed record RemoteError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

// ===== 各方法参数 =====

/// <summary>pair 参数：远程端提供设备名与本地 UI 显示的配对 PIN。</summary>
public sealed record RemotePairParams
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    [JsonPropertyName("pin")]
    public string Pin { get; init; } = string.Empty;
}

/// <summary>triggerShutdown / cancelShutdown 参数：稳定本地任务 id。</summary>
public sealed record RemoteTaskParams
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;
}

/// <summary>配对成功响应结果：交付 deviceId 与 sharedSecret（仅 TLS 连接上返回）。</summary>
public sealed record RemotePairResult
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("sharedSecret")]
    public string SharedSecret { get; init; } = string.Empty;

    [JsonPropertyName("serverName")]
    public string ServerName { get; init; } = string.Empty;
}

/// <summary>triggerShutdown 响应结果：四种场景之一。</summary>
public sealed record RemoteTriggerResult
{
    /// <summary>"equivalent"（TLS+无人值守等效确认，已执行）/ "countdown"（回退本地倒计时）/ "cancelled"。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>queryStatus 响应结果（只读状态快照，绝不包含敏感材料）。</summary>
public sealed record RemoteStatusResult
{
    [JsonPropertyName("serverName")]
    public string ServerName { get; init; } = string.Empty;

    /// <summary>调度引擎状态（Running/Faulted/Stopped/Created）。</summary>
    [JsonPropertyName("engineStatus")]
    public string EngineStatus { get; init; } = string.Empty;

    /// <summary>当前活跃实例数（Waiting/Confirming/Running/Executing）。</summary>
    [JsonPropertyName("activeCount")]
    public int ActiveCount { get; init; }

    /// <summary>当前处于倒计时（Confirming）的实例数。</summary>
    [JsonPropertyName("pendingCountdownCount")]
    public int PendingCountdownCount { get; init; }

    [JsonPropertyName("faultMessage")]
    public string? FaultMessage { get; init; }
}

/// <summary>listTasks 响应里的单个任务条目（只读；不含任何策略/白名单内容）。</summary>
public sealed record RemoteTaskInfo
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>当前实例状态（无实例为 null）。</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("scheduledFireTimeUtc")]
    public DateTimeOffset? ScheduledFireTimeUtc { get; init; }
}

/// <summary>listTasks 响应结果。</summary>
public sealed record RemoteListTasksResult
{
    [JsonPropertyName("tasks")]
    public IReadOnlyList<RemoteTaskInfo> Tasks { get; init; } = [];
}

/// <summary>cancelShutdown 响应结果。</summary>
public sealed record RemoteActionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>audit 日志条目。绝不包含 PIN/secret/HMAC/私钥。</summary>
public sealed record RemoteAuditEntry
{
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>已配对设备 id（未配对请求为空）。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>来源 IP（仅审计，不用于鉴权）。</summary>
    public string SourceIp { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public RemoteAuditOutcome Outcome { get; init; }

    public string Message { get; init; } = string.Empty;
}

public enum RemoteAuditOutcome
{
    Unknown = 0,
    Succeeded = 1,
    Rejected = 2,
    Unauthorized = 3,
    Forbidden = 4,
    Invalid = 5,
    PairingSucceeded = 6,
    PairingFailed = 7,
    PairingLocked = 8,
    TlsBlocked = 9,
    Error = 10
}
