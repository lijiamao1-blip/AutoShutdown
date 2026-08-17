using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S23 CP3 请求处理器：pair（仅 TLS）/ HMAC 鉴权 / 独立远程白名单（默认只读）/
/// triggerShutdown（等效 vs 倒计时 vs 拒绝）/ cancelShutdown（仅 Confirming）/
/// 错误码映射。全程替身引擎 + 替身任务服务 + 真实 Pairing/Authenticator；
/// 处理器绝不直接调用电源，绝不写任何配置/白名单/策略。
/// </summary>
public sealed class S23_CP3_RemoteRequestHandlerTests
{
    private const string Pin = "123456";
    private const string SourceIp = "192.168.1.5";
    private static readonly DateTimeOffset Base = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();

    // ===== pair =====

    [Fact]
    public async Task Pair_OverPlainConnection_TlsRequired()
    {
        using var harness = new HandlerHarness();
        harness.RotatePin();

        var response = await harness.CallPair(isTls: false);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.TlsRequired, response.Error!.Code);
    }

    [Fact]
    public async Task Pair_OverTls_ReturnsDeviceAndSecret()
    {
        using var harness = new HandlerHarness();
        harness.RotatePin();

        var response = await harness.CallPair(isTls: true);

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemotePairResult>()!;
        Assert.False(string.IsNullOrEmpty(result.DeviceId));
        Assert.False(string.IsNullOrEmpty(result.SharedSecret));
        Assert.Equal(harness.ServerName, result.ServerName);
    }

    [Fact]
    public async Task Pair_WrongPin_PairingFailed()
    {
        using var harness = new HandlerHarness();
        harness.RotatePin();

        var (raw, payload) = BuildPayload("pair", string.Empty, "n", BaseMs,
            "{\"deviceName\":\"PC-B\",\"pin\":\"000000\"}");
        var context = harness.Context(raw, payload, secret: null, isTls: true);
        var response = await harness.Handler.HandleAsync(context, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.PairingFailed, response.Error!.Code);
    }

    // ===== 鉴权 =====

    [Fact]
    public async Task QueryStatus_WrongHmac_Unauthorized()
    {
        using var harness = await PairAndGetSecretAsync();

        var response = await harness.CallAuthed(
            "queryStatus", harness.DeviceId, "nonce-q1", BaseMs, "{}", secret: null);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Unauthorized, response.Error!.Code);
    }

    [Fact]
    public async Task QueryStatus_UnknownDevice_Unauthorized()
    {
        using var harness = new HandlerHarness();
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "queryStatus", "unknown-device", "nonce-u", BaseMs, "{}", secret: new byte[32]);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Unauthorized, response.Error!.Code);
    }

    // ===== 只读方法 =====

    [Fact]
    public async Task QueryStatus_ValidAuth_Served_ReadOnly()
    {
        using var harness = await PairAndGetSecretAsync();
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "queryStatus", harness.DeviceId, "nonce-q2", BaseMs, "{}", harness.Secret);

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemoteStatusResult>()!;
        Assert.Equal(harness.ServerName, result.ServerName);
        Assert.Equal("Running", result.EngineStatus);
        // 默认白名单允许 queryStatus。
        Assert.Equal(0, result.ActiveCount);
    }

    [Fact]
    public async Task QueryStatus_WhiteListDenied_Forbidden()
    {
        using var harness = await PairAndGetSecretAsync();
        harness.WhiteList = new RemoteCommandWhiteList { QueryStatus = false };

        var response = await harness.CallAuthed(
            "queryStatus", harness.DeviceId, "nonce-q3", BaseMs, "{}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
    }

    [Fact]
    public async Task ListTasks_Served_ReadOnly()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.TaskService.Seed(new TaskDefinition
        {
            Id = taskId,
            Kind = TaskKind.DailyAt,
            Action = PowerAction.Shutdown,
            CreatedAt = Base,
            IsEnabled = true
        });
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "listTasks", harness.DeviceId, "nonce-l1", BaseMs, "{}", harness.Secret);

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemoteListTasksResult>()!;
        var task = Assert.Single(result.Tasks);
        Assert.Equal(taskId.ToString("D"), task.TaskId);
        Assert.Equal("DailyAt", task.Kind);
        Assert.True(task.Enabled);
    }

    [Fact]
    public async Task ListTasks_WhiteListDenied_Forbidden()
    {
        using var harness = await PairAndGetSecretAsync();
        harness.WhiteList = new RemoteCommandWhiteList { ListTasks = false };

        var response = await harness.CallAuthed(
            "listTasks", harness.DeviceId, "nonce-l2", BaseMs, "{}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
    }

    // ===== triggerShutdown =====

    [Fact]
    public async Task TriggerShutdown_DefaultWhiteList_Forbidden()
    {
        using var harness = await PairAndGetSecretAsync();

        var response = await harness.CallAuthed(
            "triggerShutdown", harness.DeviceId, "nonce-t1", BaseMs,
            "{\"taskId\":\"" + Guid.NewGuid().ToString("D") + "\"}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
        // 默认白名单禁用 triggerShutdown：引擎不应收到任何命令。
        Assert.Empty(harness.Engine.Submitted);
    }

    [Fact]
    public async Task TriggerShutdown_Manual_Tls_CountdownMode()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.TaskService.Seed(ManualTask(taskId));
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };
        var future = Base.AddSeconds(RemoteProtocol.FallbackCountdownSeconds);
        harness.Engine.OnSubmit = command => SuccessResult(ConfirmingSnapshot(taskId, future));
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "triggerShutdown", harness.DeviceId, "nonce-t2", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemoteTriggerResult>()!;
        Assert.Equal("countdown", result.Mode);
        // 引擎只收到一次命令，且等效标志对人工任务为 false。
        var submitted = Assert.IsType<RemoteTriggerTaskCommand>(Assert.Single(harness.Engine.Submitted));
        Assert.False(submitted.EquivalentUnattendedAllowed);
    }

    [Fact]
    public async Task TriggerShutdown_Unattended_Tls_EquivalentMode()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.TaskService.Seed(ManualTask(taskId, useUnattended: true));
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };
        harness.Engine.OnSubmit = command => SuccessResult(ExecutedSnapshot(taskId));
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "triggerShutdown", harness.DeviceId, "nonce-t3", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemoteTriggerResult>()!;
        Assert.Equal("equivalent", result.Mode);
        var submitted = Assert.IsType<RemoteTriggerTaskCommand>(Assert.Single(harness.Engine.Submitted));
        Assert.True(submitted.EquivalentUnattendedAllowed);
    }

    [Fact]
    public async Task TriggerShutdown_Unattended_NoTls_ExecutionRejected()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.TaskService.Seed(ManualTask(taskId, useUnattended: true));
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };

        var response = await harness.CallAuthed(
            "triggerShutdown", harness.DeviceId, "nonce-t4", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret,
            isTls: false);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.ExecutionRejected, response.Error!.Code);
        // 无人值守无 TLS：不提交引擎，fail-closed。
        Assert.Empty(harness.Engine.Submitted);
    }

    [Fact]
    public async Task TriggerShutdown_UnknownTask_TaskNotFound()
    {
        using var harness = await PairAndGetSecretAsync();
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };
        var taskId = Guid.NewGuid();

        var response = await harness.CallAuthed(
            "triggerShutdown", harness.DeviceId, "nonce-t5", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.TaskNotFound, response.Error!.Code);
        Assert.Empty(harness.Engine.Submitted);
    }

    [Fact]
    public async Task TriggerShutdown_DisabledTask_TaskDisabled()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.TaskService.Seed(ManualTask(taskId) with { IsEnabled = false });
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };

        var response = await harness.CallAuthed(
            "triggerShutdown", harness.DeviceId, "nonce-t6", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.TaskDisabled, response.Error!.Code);
        Assert.Empty(harness.Engine.Submitted);
    }

    [Fact]
    public async Task TriggerShutdown_InvalidTaskId_InvalidParams()
    {
        using var harness = await PairAndGetSecretAsync();
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };

        var response = await harness.CallAuthed(
            "triggerShutdown", harness.DeviceId, "nonce-t7", BaseMs,
            "{\"taskId\":\"not-a-guid\"}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.InvalidParams, response.Error!.Code);
    }

    // ===== cancelShutdown =====

    [Fact]
    public async Task CancelShutdown_DefaultWhiteList_Forbidden()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();

        var response = await harness.CallAuthed(
            "cancelShutdown", harness.DeviceId, "nonce-c1", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
        Assert.Empty(harness.Engine.Submitted);
    }

    [Fact]
    public async Task CancelShutdown_Enabled_Success()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.WhiteList = new RemoteCommandWhiteList { CancelShutdown = true };
        harness.Engine.OnSubmit = command => SuccessResult(ConfirmingSnapshot(taskId, Base));
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "cancelShutdown", harness.DeviceId, "nonce-c2", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemoteActionResult>()!;
        Assert.True(result.Ok);
        var submitted = Assert.IsType<RemoteCancelTaskCommand>(Assert.Single(harness.Engine.Submitted));
        Assert.Equal(taskId, submitted.TaskId);
    }

    [Fact]
    public async Task CancelShutdown_NotInCountdown_ExecutionRejected()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.WhiteList = new RemoteCommandWhiteList { CancelShutdown = true };
        harness.Engine.OnSubmit = command => RejectResult(
            SchedulerCommandStatus.TransitionRejected, "only Confirming");
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "cancelShutdown", harness.DeviceId, "nonce-c3", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.ExecutionRejected, response.Error!.Code);
    }

    [Fact]
    public async Task CancelShutdown_EngineNotRunning_ServerNotReady()
    {
        using var harness = await PairAndGetSecretAsync();
        var taskId = Guid.NewGuid();
        harness.WhiteList = new RemoteCommandWhiteList { CancelShutdown = true };
        harness.Engine.OnSubmit = command => RejectResult(
            SchedulerCommandStatus.NotRunning, "engine stopped");
        harness.Engine.Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running };

        var response = await harness.CallAuthed(
            "cancelShutdown", harness.DeviceId, "nonce-c4", BaseMs,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.ServerNotReady, response.Error!.Code);
    }

    // ===== 协议 =====

    [Fact]
    public async Task UnsupportedVersion_UnsupportedVersionError()
    {
        using var harness = new HandlerHarness();
        harness.RotatePin();

        var raw = "{\"version\":99,\"deviceId\":\"\",\"timestamp\":" + BaseMs
            + ",\"nonce\":\"n\",\"method\":\"pair\",\"params\":{\"deviceName\":\"PC-B\",\"pin\":\"123456\"}}";
        using var doc = JsonDocument.Parse(raw);
        var payload = new RemotePayload
        {
            Version = 99,
            DeviceId = string.Empty,
            Timestamp = BaseMs,
            Nonce = "n",
            Method = "pair",
            Params = doc.RootElement.GetProperty("params").Clone()
        };
        var context = new RemoteRequestContext
        {
            WhiteList = new RemoteCommandWhiteList(),
            IsTls = true,
            SourceIp = SourceIp,
            Envelope = new RemoteRequestEnvelope { JsonRpc = "2.0", Id = 1, Payload = raw },
            Payload = payload
        };

        var response = await harness.Handler.HandleAsync(context, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.UnsupportedVersion, response.Error!.Code);
    }

    [Fact]
    public async Task UnknownMethod_MethodNotFound()
    {
        using var harness = await PairAndGetSecretAsync();

        var response = await harness.CallAuthed(
            "nukeEverything", harness.DeviceId, "nonce-m1", BaseMs, "{}", harness.Secret);

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.MethodNotFound, response.Error!.Code);
    }

    // ===== 替身 =====

    private static TaskDefinition ManualTask(Guid id, bool useUnattended = false) => new()
    {
        Id = id,
        Kind = TaskKind.DailyAt,
        Action = PowerAction.Shutdown,
        CreatedAt = Base,
        RealPowerConfirmed = true,
        UseUnattended = useUnattended,
        IsEnabled = true
    };

    private static SchedulerSnapshot ConfirmingSnapshot(Guid taskId, DateTimeOffset fire) => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        Instances = new Dictionary<Guid, TaskInstance>
        {
            [taskId] = new()
            {
                InstanceId = Guid.NewGuid(),
                SourceTaskId = taskId,
                State = TaskInstanceState.Confirming,
                ScheduledFireTime = fire,
                CreatedAt = Base
            }
        }
    };

    private static SchedulerSnapshot ExecutedSnapshot(Guid taskId) => new()
    {
        EngineStatus = SchedulerEngineStatus.Running,
        Instances = new Dictionary<Guid, TaskInstance>
        {
            [taskId] = new()
            {
                InstanceId = Guid.NewGuid(),
                SourceTaskId = taskId,
                State = TaskInstanceState.Executed,
                ScheduledFireTime = Base,
                CreatedAt = Base
            }
        }
    };

    private static SchedulerCommandResult SuccessResult(SchedulerSnapshot snapshot) => new()
    {
        Status = SchedulerCommandStatus.Success,
        Snapshot = snapshot,
        Message = "ok"
    };

    private static SchedulerCommandResult RejectResult(SchedulerCommandStatus status, string message) => new()
    {
        Status = status,
        Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running },
        Message = message
    };

    private static (string Raw, RemotePayload Payload) BuildPayload(
        string method,
        string deviceId,
        string nonce,
        long timestamp,
        string paramsJson)
    {
        var raw = "{\"version\":1,\"deviceId\":\"" + deviceId
            + "\",\"timestamp\":" + timestamp
            + ",\"nonce\":\"" + nonce
            + "\",\"method\":\"" + method
            + "\",\"params\":" + paramsJson + "}";
        using var doc = JsonDocument.Parse(raw);
        var payload = new RemotePayload
        {
            Version = 1,
            DeviceId = deviceId,
            Timestamp = timestamp,
            Nonce = nonce,
            Method = method,
            Params = doc.RootElement.GetProperty("params").Clone()
        };
        return (raw, payload);
    }

    private static string Hmac(byte[] secret, string raw)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static async Task<HandlerHarness> PairAndGetSecretAsync()
    {
        var harness = new HandlerHarness();
        harness.RotatePin();
        var response = await harness.CallPair(isTls: true);
        Assert.Null(response.Error);
        var pair = response.Result!.Value.Deserialize<RemotePairResult>()!;
        harness.DeviceId = pair.DeviceId;
        harness.Secret = Convert.FromBase64String(pair.SharedSecret);
        return harness;
    }

    private sealed class HandlerHarness : IDisposable
    {
        public CapturingStorage Storage { get; }
        public FakeClock Clock { get; }
        public PairingService Pairing { get; }
        public RemoteAuthenticator Auth { get; }
        public FakeSchedulerEngine Engine { get; }
        public FakeTaskService TaskService { get; }
        public RemoteRequestHandler Handler { get; }
        public List<RemoteAuditEntry> Audit { get; }
        public string ServerName { get; } = "TestPC";
        public RemoteCommandWhiteList WhiteList { get; set; } = new();
        public string DeviceId { get; set; } = string.Empty;
        public byte[] Secret { get; set; } = [];

        public HandlerHarness()
        {
            Storage = new CapturingStorage();
            Clock = new FakeClock(Base);
            Audit = [];
            Pairing = new PairingService(
                new RemoteDevicesStore(Storage),
                new RemotePairingLockStore(Storage),
                new FakeSecretProtector(),
                Clock,
                new FakeAuditLog(Audit),
                pinGenerator: () => Pin,
                secretGenerator: () => Enumerable.Range(0, RemoteProtocol.SharedSecretBytes)
                    .Select(i => (byte)(i + 1)).ToArray());
            Auth = new RemoteAuthenticator(Pairing, Clock);
            Engine = new FakeSchedulerEngine();
            TaskService = new FakeTaskService();
            Handler = new RemoteRequestHandler(
                Pairing,
                Auth,
                Engine,
                TaskService,
                Clock,
                new FakeAuditLog(Audit),
                () => ServerName);
        }

        public void RotatePin() => Pairing.RotatePin();

        public Task<RemoteResponse> CallPair(bool isTls)
        {
            var (raw, payload) = BuildPayload("pair", string.Empty, "n", BaseMs,
                "{\"deviceName\":\"PC-B\",\"pin\":\"" + Pin + "\"}");
            return Handler.HandleAsync(Context(raw, payload, secret: null, isTls), CancellationToken.None);
        }

        public Task<RemoteResponse> CallAuthed(
            string method,
            string deviceId,
            string nonce,
            long timestamp,
            string paramsJson,
            byte[]? secret,
            bool isTls = true)
        {
            var (raw, payload) = BuildPayload(method, deviceId, nonce, timestamp, paramsJson);
            var envelope = new RemoteRequestEnvelope
            {
                JsonRpc = "2.0",
                Id = 1,
                Payload = raw,
                Hmac = secret is null ? "deadbeef" : Hmac(secret, raw)
            };
            var context = new RemoteRequestContext
            {
                WhiteList = WhiteList,
                IsTls = isTls,
                SourceIp = SourceIp,
                Envelope = envelope,
                Payload = payload
            };
            return Handler.HandleAsync(context, CancellationToken.None);
        }

        public RemoteRequestContext Context(
            string raw,
            RemotePayload payload,
            byte[]? secret,
            bool isTls)
        {
            return new RemoteRequestContext
            {
                WhiteList = WhiteList,
                IsTls = isTls,
                SourceIp = SourceIp,
                Envelope = new RemoteRequestEnvelope
                {
                    JsonRpc = "2.0",
                    Id = 1,
                    Payload = raw,
                    Hmac = secret is null ? string.Empty : Hmac(secret, raw)
                },
                Payload = payload
            };
        }

        public void Dispose() { }
    }

    private sealed class FakeSchedulerEngine : ISchedulerEngine
    {
        public SchedulerSnapshot Snapshot { get; set; } =
            new() { EngineStatus = SchedulerEngineStatus.Running };

        public Func<SchedulerCommand, SchedulerCommandResult> OnSubmit { get; set; } =
            _ => new SchedulerCommandResult
            {
                Status = SchedulerCommandStatus.Success,
                Snapshot = new SchedulerSnapshot { EngineStatus = SchedulerEngineStatus.Running },
                Message = "ok"
            };

        public List<SchedulerCommand> Submitted { get; } = new();

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Submitted.Add(command);
            return Task.FromResult(OnSubmit(command));
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

        public void Seed(params TaskDefinition[] definitions)
        {
            foreach (var definition in definitions)
            {
                _definitions[definition.Id] = definition;
            }
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

    private sealed class CapturingStorage : IStorage
    {
        private readonly InMemoryStorage _inner = new();

        public void Seed(string path, string json) => _inner.Seed(path, json);

        public Task<StorageReadResult<T>> ReadAsync<T>(string relativePath, CancellationToken cancellationToken)
            => _inner.ReadAsync<T>(relativePath, cancellationToken);

        public Task<StorageWriteResult> WriteAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(value);
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
