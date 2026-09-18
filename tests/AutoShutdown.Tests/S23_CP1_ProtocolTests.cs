using System.Text.Json;
using AutoShutdown.Core.Remote;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>S23 CP1：威胁模型与协议冻结。验证方法名、错误码、消息大小、时间窗、nonce 生命周期
/// 常量与远程命令白名单默认值（默认仅只读，触发需显式启用）。</summary>
public sealed class S23_CP1_ProtocolTests
{
    [Fact]
    public void ProtocolConstants_AreFrozenAsSpecified()
    {
        Assert.Equal("2.0", RemoteProtocol.JsonRpcVersion);
        Assert.Equal(1, RemoteProtocol.ProtocolVersion);
        Assert.Equal(64 * 1024, RemoteProtocol.MaxRequestBytes);
        Assert.Equal(5 * 60 * 1000, RemoteProtocol.TimestampToleranceMs);

        // nonce TTL 由「与时间窗一致」修正为「时间窗的 2 倍」（S-REMOTE-D2）。
        //
        // 原因：时间戳判据 |now - timestamp| <= 容差 同时容忍落后与超前各一个容差，
        // 因此一条请求最长可在首次到达后 2×容差 的时间内仍然通过时间戳校验。
        // TTL 只取 1×容差 时，nonce 会在请求仍处于有效时间窗内被清理掉，
        // 留下一段可以把抓到的请求原样重放一次的窗口。TTL 必须完整覆盖时间戳有效期。
        //
        // 这不是线路协议变更：nonce TTL 只是服务端防重放缓存的保留时长，
        // 不出现在任何请求/响应字段里，已配对设备无需任何改动。
        //
        // 两条断言都保留：相对断言锁住「TTL = 2×容差」这条不变量，
        // 绝对断言锁住具体数值，使得日后调整容差时必须显式复核 TTL。
        Assert.Equal(2 * RemoteProtocol.TimestampToleranceMs, RemoteProtocol.NonceTtlMs);
        Assert.Equal(10 * 60 * 1000, RemoteProtocol.NonceTtlMs);
        Assert.Equal(5, RemoteProtocol.PairingMaxFailedAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), RemoteProtocol.PairingLockDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), RemoteProtocol.PairingPinLifetime);
        Assert.Equal(6, RemoteProtocol.PairingPinLength);
        Assert.Equal(32, RemoteProtocol.SharedSecretBytes);
        Assert.Equal(60, RemoteProtocol.FallbackCountdownSeconds);
    }

    [Fact]
    public void MethodNames_AreFrozen()
    {
        Assert.Equal("pair", RemoteProtocol.MethodPair);
        Assert.Equal("queryStatus", RemoteProtocol.MethodQueryStatus);
        Assert.Equal("listTasks", RemoteProtocol.MethodListTasks);
        Assert.Equal("triggerShutdown", RemoteProtocol.MethodTriggerShutdown);
        Assert.Equal("cancelShutdown", RemoteProtocol.MethodCancelShutdown);
    }

    [Fact]
    public void RemoteCommandWhiteList_DefaultsToReadOnlyOnly()
    {
        var whitelist = new RemoteCommandWhiteList();

        Assert.True(whitelist.QueryStatus, "queryStatus must be allowed by default.");
        Assert.True(whitelist.ListTasks, "listTasks must be allowed by default.");
        Assert.False(whitelist.TriggerShutdown, "triggerShutdown must require explicit local enable.");
        Assert.False(whitelist.CancelShutdown, "cancelShutdown must require explicit local enable.");
        Assert.Empty(RemoteCommandWhiteListValidator.Validate(whitelist));
    }

    [Fact]
    public void RemoteCommandWhiteList_NullIsInvalid()
    {
        var errors = RemoteCommandWhiteListValidator.Validate(null);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.Contains("WhiteList", StringComparison.Ordinal));
    }

    [Fact]
    public void RequestEnvelope_SerializesRoundTrip_WithExactPayloadBytes()
    {
        const string payload =
            "{\"version\":1,\"deviceId\":\"dev-1\",\"timestamp\":1723900000000,\"nonce\":\"n1\",\"method\":\"queryStatus\",\"params\":{}}";
        var envelope = new RemoteRequestEnvelope
        {
            Id = 7,
            Payload = payload,
            Hmac = "abcd"
        };

        var json = JsonSerializer.Serialize(envelope);
        var parsed = JsonSerializer.Deserialize<RemoteRequestEnvelope>(json);

        Assert.NotNull(parsed);
        Assert.Equal(RemoteProtocol.JsonRpcVersion, parsed!.JsonRpc);
        Assert.Equal(7, parsed.Id);
        Assert.Equal(payload, parsed.Payload);
        Assert.Equal("abcd", parsed.Hmac);
    }

    [Fact]
    public void RemotePayload_ParsesFromJsonElement()
    {
        const string payload =
            "{\"version\":1,\"deviceId\":\"dev-1\",\"timestamp\":1723900000000,\"nonce\":\"n1\",\"method\":\"triggerShutdown\",\"params\":{\"taskId\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\"}}";
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("triggerShutdown", root.GetProperty("method").GetString());
        Assert.Equal("dev-1", root.GetProperty("deviceId").GetString());
        Assert.Equal(1723900000000L, root.GetProperty("timestamp").GetInt64());
        Assert.Equal("n1", root.GetProperty("nonce").GetString());

        var taskParams = root.GetProperty("params").Deserialize<RemoteTaskParams>();
        Assert.NotNull(taskParams);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00cf4fc964ff", taskParams!.TaskId);
    }

    [Fact]
    public void RemotePairParams_Deserializes()
    {
        const string paramsJson = "{\"deviceName\":\"PC-B\",\"pin\":\"123456\"}";
        using var doc = JsonDocument.Parse(paramsJson);
        var pair = doc.RootElement.Deserialize<RemotePairParams>();

        Assert.NotNull(pair);
        Assert.Equal("PC-B", pair!.DeviceName);
        Assert.Equal("123456", pair.Pin);
    }

    [Fact]
    public void ErrorCodes_AreDistinct()
    {
        var codes = Enum.GetValues<RemoteErrorCode>();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Contains(RemoteErrorCode.Unauthorized, codes);
        Assert.Contains(RemoteErrorCode.PairingLocked, codes);
        Assert.Contains(RemoteErrorCode.Forbidden, codes);
        Assert.Contains(RemoteErrorCode.TlsRequired, codes);
    }

    [Fact]
    public void TriggerResult_ModesAreEnumerated()
    {
        // 冻结语义：equivalent = TLS+无人值守等效确认直接执行；countdown = 回退本地倒计时。
        var equivalent = new RemoteTriggerResult { Mode = "equivalent" };
        var countdown = new RemoteTriggerResult { Mode = "countdown" };

        Assert.Equal("equivalent", equivalent.Mode);
        Assert.Equal("countdown", countdown.Mode);
    }
}
