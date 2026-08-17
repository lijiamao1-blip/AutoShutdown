using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Configuration;
using AutoShutdown.Core.Power;
using AutoShutdown.Core.Remote;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using AutoShutdown.Core.Unattended;
using AutoShutdown.Core.Workflow;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S23 CP4 命令组合矩阵与契约（真实引擎 + 真实 handler + RecordingPowerService）：
/// <list type="bullet">
/// <item>四场景：A) TLS+白名单+无人值守 → 等效确认，恰好 1 次电源调用；
/// B) TLS+白名单+人工确认 → 本地倒计时回退，到期经边界执行，恰好 1 次电源；
/// C) 明文+白名单+无人值守（无 TLS）→ fail-closed 拒绝，0 次电源、不创建实例；
/// D) TLS+白名单关闭 → Forbidden，0 次电源、引擎未收到任何远程命令。</item>
/// <item>只读命令（queryStatus/listTasks）绝不向引擎提交任何命令、绝不触碰电源。</item>
/// <item>整段远程会话后本地文档（config.json / unattended.json / remote-settings.json）
/// 内容不变，且写入路径绝不包含这三个文档（只允许配对写 remote-devices 等远程存储）。</item>
/// <item>远程白名单（remote-settings.json）与本地 config.json（S19 RunCommands 白名单）完全独立。</item>
/// </list>
/// 任何路径绝不直接调用 IPowerService（唯一出口是本地引擎的 handler/workflow 双闸门）。
/// </summary>
public sealed class S23_CP4_CommandMatrixTests
{
    private const string Pin = "123456";
    private const string SourceIp = "192.168.1.5";
    private static readonly DateTimeOffset Base = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();
    private static readonly TimeZoneInfo FixedUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "FixedUtc8",
        TimeSpan.FromHours(8),
        "FixedUtc8",
        "FixedUtc8");

    // ================= 四场景矩阵（真实引擎 + 真实 handler + RecordingPowerService） =================

    // 场景A：TLS + 白名单(trigger开) + 无人值守任务 → 等效确认立即执行，恰好 1 次电源调用。
    [Fact(Timeout = 3000)]
    public async Task ScenarioA_TlsUnattended_Equivalent_ExactlyOnePowerCall()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateMatrix(power, policy, DailyAtTask(taskId, useUnattended: true));
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };
        var secret = await harness.PairOverTlsAsync();

        var response = await harness.TriggerShutdownAsync(taskId, secret, isTls: true, "nA");

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemoteTriggerResult>()!;
        Assert.Equal("equivalent", result.Mode);
        Assert.Single(power.Requests);
        Assert.Equal(1, harness.Handler.CallCount);
        // 周期任务等效执行后改期回 Waiting（实例仍存于本地引擎这一唯一事实源）。
        Assert.Equal(TaskInstanceState.Waiting, Current(harness.Engine.GetSnapshot())!.State);
        Assert.Single(harness.RemoteEngine.RemoteTriggers);
        Assert.Empty(harness.RemoteEngine.RemoteCancels);
    }

    // 场景B：TLS + 白名单(trigger开) + 人工确认任务 → 本地倒计时回退；到期经边界执行，恰好 1 次电源。
    [Fact(Timeout = 3000)]
    public async Task ScenarioB_TlsManual_Countdown_NoPowerUntilFire_ThenOne()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateMatrix(power, policy, DailyAtTask(taskId));
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };
        var secret = await harness.PairOverTlsAsync();

        var response = await harness.TriggerShutdownAsync(taskId, secret, isTls: true, "nB");

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize<RemoteTriggerResult>()!;
        Assert.Equal("countdown", result.Mode);
        Assert.Empty(power.Requests);
        Assert.Equal(0, harness.Handler.CallCount);
        var instance = Current(harness.Engine.GetSnapshot());
        Assert.NotNull(instance);
        Assert.Equal(TaskInstanceState.Confirming, instance!.State);
        Assert.Equal(Base.AddSeconds(RemoteProtocol.FallbackCountdownSeconds), instance.ScheduledFireTime);

        // 到期：推进时钟 + 完成 deadline → 倒计时边界裁决并执行（本地唯一路径，绝不重复）。
        harness.Clock.UtcNow = Base.AddSeconds(RemoteProtocol.FallbackCountdownSeconds);
        await WaitUntilAsync(() => harness.Deadline.PendingCount >= 1);
        harness.Deadline.CompleteNext();
        await WaitUntilAsync(() => harness.Handler.CallCount >= 1);

        Assert.Single(power.Requests);
    }

    // 场景C：明文(无TLS) + 白名单(trigger开) + 无人值守任务 → 无等效确认 → fail-closed 拒绝，
    // 0 次电源、不创建任何实例（绝不回退为本地倒计时绕过无人值守策略）。
    [Fact(Timeout = 3000)]
    public async Task ScenarioC_PlainUnattended_NoEquivalent_Rejected_NoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateMatrix(power, policy, DailyAtTask(taskId, useUnattended: true));
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true };
        var secret = await harness.PairOverTlsAsync();

        var response = await harness.TriggerShutdownAsync(taskId, secret, isTls: false, "nC");

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.ExecutionRejected, response.Error!.Code);
        Assert.Empty(power.Requests);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Null(Current(harness.Engine.GetSnapshot()));
        Assert.Empty(harness.RemoteEngine.RemoteTriggers);
    }

    // 场景D：TLS + 白名单(trigger关) + 无人值守任务 → Forbidden，0 次电源，引擎未收到任何远程命令。
    [Fact(Timeout = 3000)]
    public async Task ScenarioD_TlsWhitelistOff_Forbidden_NoEngineSubmit_NoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateMatrix(power, policy, DailyAtTask(taskId, useUnattended: true));
        harness.WhiteList = new RemoteCommandWhiteList(); // 默认：仅 queryStatus/listTasks。
        var secret = await harness.PairOverTlsAsync();

        var response = await harness.TriggerShutdownAsync(taskId, secret, isTls: true, "nD");

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
        Assert.Empty(power.Requests);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(harness.RemoteEngine.RemoteTriggers);
        Assert.Empty(harness.RemoteEngine.RemoteCancels);
        Assert.Null(Current(harness.Engine.GetSnapshot()));
    }

    // 只读命令（queryStatus/listTasks）绝不向引擎提交任何命令、绝不触碰电源。
    [Fact(Timeout = 3000)]
    public async Task ReadOnlyMethods_SubmitNothing_NoPower()
    {
        var taskId = Guid.NewGuid();
        var power = new RecordingPowerService(AcceptedResult());
        var policy = new FixedUnattendedPolicyService(_ => Authorized(PowerAction.Shutdown));
        using var harness = CreateMatrix(power, policy, DailyAtTask(taskId));
        var secret = await harness.PairOverTlsAsync();

        var status = await harness.QueryStatusAsync(secret, "nQ");
        var list = await harness.ListTasksAsync(secret, "nL");

        Assert.Null(status.Error);
        Assert.Null(list.Error);
        var statusResult = status.Result!.Value.Deserialize<RemoteStatusResult>()!;
        Assert.Equal("Running", statusResult.EngineStatus);
        var listResult = list.Result!.Value.Deserialize<RemoteListTasksResult>()!;
        Assert.Single(listResult.Tasks);
        Assert.Empty(power.Requests);
        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Empty(harness.RemoteEngine.RemoteTriggers);
        Assert.Empty(harness.RemoteEngine.RemoteCancels);
    }

    // ================= 本地文档只读 + 远程白名单独立（CapturingStorage + 假引擎 + 真实 handler） =================

    // 整段远程会话（配对/查询/触发/取消/拒绝/鉴权失败）后：
    // config.json / unattended.json / remote-settings.json 内容逐字节不变；
    // 写入路径绝不包含这三个文档（只允许配对写 remote-devices.json 等远程存储）。
    [Fact]
    public async Task LocalDocuments_Untouched_AfterFullRemoteSession()
    {
        var taskId = Guid.NewGuid();
        using var harness = new LocalHarness();
        harness.TaskService.Seed(DailyAtTask(taskId));
        harness.WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true, CancelShutdown = true };

        // 预置本地策略/配置文档（哨兵内容），证明整段远程会话对它们逐字节零改动。
        harness.Storage.Seed("config.json", "{\"schemaVersion\":1,\"testMode\":false}");
        harness.Storage.Seed("unattended.json", "{\"authorizationVersion\":3,\"enabled\":true}");

        var beforeConfig = await SnapshotJsonAsync(harness.Storage, "config.json");
        var beforeUnattended = await SnapshotJsonAsync(harness.Storage, "unattended.json");
        var beforeSettings = await SnapshotJsonAsync(harness.Storage, RemoteSettingsStore.FileName);

        var pair = await harness.PairAsync(isTls: true);
        Assert.Null(pair.Error);
        var pairResult = pair.Result!.Value.Deserialize<RemotePairResult>()!;
        harness.DeviceId = pairResult.DeviceId;
        harness.Secret = Convert.FromBase64String(pairResult.SharedSecret);

        await harness.QueryStatusAsync("n1");
        await harness.ListTasksAsync("n2");
        var triggered = await harness.TriggerShutdownAsync(taskId, "n3");
        Assert.Null(triggered.Error);
        var cancelled = await harness.CancelShutdownAsync(taskId, "n4");
        Assert.Null(cancelled.Error);
        var missing = await harness.TriggerShutdownAsync(Guid.NewGuid(), "n6");
        Assert.NotNull(missing.Error);
        Assert.Equal((int)RemoteErrorCode.TaskNotFound, missing.Error!.Code);
        var badAuth = await harness.CallAuthedAsync("queryStatus", "unknown-device", "n7", BaseMs, "{}", new byte[32]);
        Assert.Equal((int)RemoteErrorCode.Unauthorized, badAuth.Error!.Code);

        var afterConfig = await SnapshotJsonAsync(harness.Storage, "config.json");
        var afterUnattended = await SnapshotJsonAsync(harness.Storage, "unattended.json");
        var afterSettings = await SnapshotJsonAsync(harness.Storage, RemoteSettingsStore.FileName);

        Assert.Equal(beforeConfig, afterConfig);
        Assert.Equal(beforeUnattended, afterUnattended);
        Assert.Equal(beforeSettings, afterSettings);
        Assert.DoesNotContain("config.json", harness.Storage.WritePaths);
        Assert.DoesNotContain("unattended.json", harness.Storage.WritePaths);
        Assert.DoesNotContain(RemoteSettingsStore.FileName, harness.Storage.WritePaths);
        // 配对合法写入远程设备存储，但绝不触碰本地策略/配置/远程白名单。
        Assert.Contains("remote-devices.json", harness.Storage.WritePaths);
    }

    // 远程白名单（remote-settings.json）与本地 config.json（S19 RunCommands 白名单）完全独立：
    // 本地配置即使授权关机命令，远程 triggerShutdown 仍按独立远程白名单裁决（默认拒绝）。
    [Fact]
    public async Task RemoteWhiteList_Independent_FromLocalRunCommandsConfig()
    {
        using var harness = new LocalHarness();

        // 本地 config.json 含 RunCommands 白名单（授权关机命令）——证明本地白名单是「开着」的。
        harness.Storage.Seed("config.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            testMode = false,
            runCommands = new
            {
                enabled = true,
                whitelist = new
                {
                    entries = new[]
                    {
                        new { id = "c1", executable = "C:\\shutdown.bat", arguments = "", enabled = true }
                    }
                }
            }
        }));

        // 远程白名单默认（仅只读）：triggerShutdown 未启用，与本地 config 完全无关。
        var settingsStore = new RemoteSettingsStore(harness.Storage);
        var load = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.Equal(RemoteSettingsLoadStatus.Success, load.Status);
        Assert.False(load.Document!.WhiteList.TriggerShutdown);
        Assert.True(load.Document.WhiteList.QueryStatus);

        // 把服务器从 remote-settings.json 读出的白名单交给处理器，即使本地 config 授权关机，
        // 远程 triggerShutdown 仍被独立远程白名单拒绝。
        harness.WhiteList = load.Document.WhiteList;
        var pair = await harness.PairAsync(isTls: true);
        Assert.Null(pair.Error);
        var pairResult = pair.Result!.Value.Deserialize<RemotePairResult>()!;
        harness.DeviceId = pairResult.DeviceId;
        harness.Secret = Convert.FromBase64String(pairResult.SharedSecret);
        var response = await harness.TriggerShutdownAsync(Guid.NewGuid(), "n8");
        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
    }

    // ================= 工具 =================

    private static async Task<string?> SnapshotJsonAsync(IStorage storage, string path)
    {
        var read = await storage.ReadAsync<JsonElement>(path, CancellationToken.None);
        return read.Status == StorageReadStatus.Success ? read.Value.GetRawText() : null;
    }

    private static TaskInstance? Current(SchedulerSnapshot snapshot)
        => snapshot.Instances.Values.FirstOrDefault();

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for the condition.");
            }

            await Task.Delay(5);
        }
    }

    private static UnattendedAuthorizationDecision Authorized(PowerAction action)
        => UnattendedAuthorizationDecision.Granted(new UnattendedPolicy
        {
            Enabled = true,
            AuthorizationVersion = 3,
            AuthorizedAtUtc = Base,
            AuthorizedAction = action,
            TriggerReason = "remote"
        });

    private static PowerResult AcceptedResult() => new()
    {
        Outcome = PowerOutcome.Accepted,
        WasSimulated = false,
        Message = "real power accepted"
    };

    private static AppConfig RealPowerConfig() => new()
    {
        SchemaVersion = 1,
        TestMode = false,
        RealPowerEnabled = true,
        AllowedActions = [PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate],
        Logging = new LoggingConfig { Level = LogLevel.Information, RetentionDays = 14 }
    };

    private static TaskDefinition DailyAtTask(
        Guid id,
        bool realPowerConfirmed = true,
        bool useUnattended = false) => new()
        {
            Id = id,
            Kind = TaskKind.DailyAt,
            Action = PowerAction.Shutdown,
            TargetTimeOfDay = new TimeOnly(9, 0, 0),
            CreatedAt = Base,
            RealPowerConfirmed = realPowerConfirmed,
            UseUnattended = useUnattended
        };

    // ---------- 矩阵夹具：真实引擎 + 真实 handler + 记录型引擎包装（远程命令计数） ----------

    private static MatrixHarness CreateMatrix(
        RecordingPowerService power,
        FixedUnattendedPolicyService policy,
        TaskDefinition seedTask)
    {
        // 必须用「写时落文档」的存储：配对会把设备写入 remote-devices.json，
        // 鉴权要回读该文档（InMemoryStorage 的 WriteAsync 只记路径不落值，不能用于配对）。
        var storage = new RecordingStorage();
        var document = new TasksDocument { Tasks = [seedTask] };
        storage.Seed(TasksDocumentStore.FileName, JsonSerializer.Serialize(document));

        var clock = new FakeClock(Base, FixedUtc8);
        var deadline = new ControllableDeadline();
        var evaluator = new UnattendedConfirmationEvaluator();
        var workflow = new ShutdownWorkflow(
            new FixedConfigurationService(RealPowerConfig()),
            power,
            unattendedPolicyService: policy,
            unattendedEvaluator: evaluator);
        var handler = new CountingHandler(new ShutdownScheduledTaskHandler(workflow));
        var identifierGenerator = new CountingIdentifierGenerator();
        var taskService = new TaskService(
            new NextExecutionCalculator(),
            new TaskInstanceStateMachine(),
            identifierGenerator);

        var engine = new SchedulerEngine(
            storage,
            clock,
            deadline,
            taskService,
            new TaskInstanceStateMachine(),
            identifierGenerator,
            handler,
            new NoOpTaskArbitrator(),
            unattendedPolicyService: policy,
            unattendedEvaluator: evaluator);

        var recording = new RecordingEngine(engine);
        var audit = new List<RemoteAuditEntry>();
        var pairing = new PairingService(
            new RemoteDevicesStore(storage),
            new RemotePairingLockStore(storage),
            new FakeSecretProtector(),
            clock,
            new FakeAuditLog(audit),
            pinGenerator: () => Pin,
            secretGenerator: () => Enumerable.Range(0, RemoteProtocol.SharedSecretBytes)
                .Select(i => (byte)(i + 1)).ToArray());
        var remote = new RemoteRequestHandler(
            pairing,
            new RemoteAuthenticator(pairing, clock),
            recording,
            taskService,
            clock,
            new FakeAuditLog(audit),
            () => "TestPC");

        var scope = new EngineScope(engine);
        WaitUntilRunning(engine);
        return new MatrixHarness(
            engine,
            recording,
            handler,
            power,
            clock,
            deadline,
            remote,
            pairing,
            scope);
    }

    private static void WaitUntilRunning(SchedulerEngine engine)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 3000)
        {
            if (engine.GetSnapshot().EngineStatus == SchedulerEngineStatus.Running)
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The scheduler engine did not become ready.");
    }

    private sealed class MatrixHarness : IDisposable
    {
        public SchedulerEngine Engine { get; }
        public RecordingEngine RemoteEngine { get; }
        public CountingHandler Handler { get; }
        public RecordingPowerService Power { get; }
        public FakeClock Clock { get; }
        public ControllableDeadline Deadline { get; }
        public RemoteRequestHandler Remote { get; }
        public PairingService Pairing { get; }
        private readonly EngineScope _scope;
        public RemoteCommandWhiteList WhiteList { get; set; } = new();
        public string ServerName => "TestPC";
        public string DeviceId { get; private set; } = string.Empty;

        public MatrixHarness(
            SchedulerEngine engine,
            RecordingEngine remoteEngine,
            CountingHandler handler,
            RecordingPowerService power,
            FakeClock clock,
            ControllableDeadline deadline,
            RemoteRequestHandler remote,
            PairingService pairing,
            EngineScope scope)
        {
            Engine = engine;
            RemoteEngine = remoteEngine;
            Handler = handler;
            Power = power;
            Clock = clock;
            Deadline = deadline;
            Remote = remote;
            Pairing = pairing;
            _scope = scope;
        }

        public void Dispose() => _scope.Dispose();

        public async Task<byte[]> PairOverTlsAsync()
        {
            Pairing.RotatePin();
            var response = await CallAsync("pair", string.Empty, "pair-n", BaseMs,
                "{\"deviceName\":\"PC-B\",\"pin\":\"" + Pin + "\"}", secret: null, isTls: true);
            Assert.Null(response.Error);
            var result = response.Result!.Value.Deserialize<RemotePairResult>()!;
            DeviceId = result.DeviceId;
            return Convert.FromBase64String(result.SharedSecret);
        }

        public Task<RemoteResponse> QueryStatusAsync(byte[] secret, string nonce)
            => CallAsync("queryStatus", DeviceId, nonce, BaseMs, "{}", secret, isTls: true);

        public Task<RemoteResponse> ListTasksAsync(byte[] secret, string nonce)
            => CallAsync("listTasks", DeviceId, nonce, BaseMs, "{}", secret, isTls: true);

        public Task<RemoteResponse> TriggerShutdownAsync(Guid taskId, byte[] secret, bool isTls, string nonce)
            => CallAsync("triggerShutdown", DeviceId, nonce, BaseMs,
                "{\"taskId\":\"" + taskId.ToString("D") + "\"}", secret, isTls);

        private async Task<RemoteResponse> CallAsync(
            string method,
            string deviceId,
            string nonce,
            long timestamp,
            string paramsJson,
            byte[]? secret,
            bool isTls)
        {
            var raw = "{\"version\":1,\"deviceId\":\"" + deviceId
                + "\",\"timestamp\":" + timestamp
                + ",\"nonce\":\"" + nonce
                + "\",\"method\":\"" + method
                + "\",\"params\":" + paramsJson + "}";
            using var document = JsonDocument.Parse(raw);
            var payload = new RemotePayload
            {
                Version = 1,
                DeviceId = deviceId,
                Timestamp = timestamp,
                Nonce = nonce,
                Method = method,
                Params = document.RootElement.GetProperty("params").Clone()
            };
            var envelope = new RemoteRequestEnvelope
            {
                JsonRpc = "2.0",
                Id = 1,
                Payload = raw,
                Hmac = secret is null ? string.Empty : Hmac(secret, raw)
            };
            var context = new RemoteRequestContext
            {
                WhiteList = WhiteList,
                IsTls = isTls,
                SourceIp = SourceIp,
                Envelope = envelope,
                Payload = payload
            };
            return await Remote.HandleAsync(context, CancellationToken.None);
        }
    }

    /// <summary>包装真实引擎，记录远程命令提交（唯一电源出口证明）。</summary>
    private sealed class RecordingEngine : ISchedulerEngine
    {
        private readonly SchedulerEngine _inner;

        public RecordingEngine(SchedulerEngine inner) => _inner = inner;

        public List<RemoteTriggerTaskCommand> RemoteTriggers { get; } = new();

        public List<RemoteCancelTaskCommand> RemoteCancels { get; } = new();

        public Task RunAsync(CancellationToken cancellationToken) => _inner.RunAsync(cancellationToken);

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            if (command is RemoteTriggerTaskCommand trigger)
            {
                RemoteTriggers.Add(trigger);
            }

            if (command is RemoteCancelTaskCommand cancel)
            {
                RemoteCancels.Add(cancel);
            }

            return _inner.SubmitAsync(command, cancellationToken);
        }

        public SchedulerSnapshot GetSnapshot() => _inner.GetSnapshot();
    }

    // ---------- 本地夹具：CapturingStorage（记录写路径）+ 假引擎 + 真实 handler ----------

    private sealed class LocalHarness : IDisposable
    {
        public RecordingStorage Storage { get; }
        public FakeClock Clock { get; }
        public PairingService Pairing { get; }
        public RecordingFakeEngine Engine { get; }
        public FakeTaskService TaskService { get; }
        public RemoteRequestHandler Handler { get; }
        public List<RemoteAuditEntry> Audit { get; }
        public string ServerName { get; } = "TestPC";
        public RemoteCommandWhiteList WhiteList { get; set; } = new();
        public string DeviceId { get; set; } = string.Empty;
        public byte[] Secret { get; set; } = [];

        public LocalHarness()
        {
            Storage = new RecordingStorage();
            Storage.Seed(RemoteSettingsStore.FileName, JsonSerializer.Serialize(new RemoteSettingsDocument()));
            Clock = new FakeClock(Base, FixedUtc8);
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
            Engine = new RecordingFakeEngine();
            TaskService = new FakeTaskService();
            Handler = new RemoteRequestHandler(
                Pairing,
                new RemoteAuthenticator(Pairing, Clock),
                Engine,
                TaskService,
                Clock,
                new FakeAuditLog(Audit),
                () => ServerName);
        }

        public void Dispose() { }

        public Task<RemoteResponse> PairAsync(bool isTls)
        {
            Pairing.RotatePin();
            return CallAuthedAsync("pair", string.Empty, "p", BaseMs,
                "{\"deviceName\":\"PC-B\",\"pin\":\"" + Pin + "\"}", secret: null, isTls);
        }

        public Task<RemoteResponse> QueryStatusAsync(string nonce)
            => CallAuthedAsync("queryStatus", DeviceId, nonce, BaseMs, "{}", Secret, isTls: true);

        public Task<RemoteResponse> ListTasksAsync(string nonce)
            => CallAuthedAsync("listTasks", DeviceId, nonce, BaseMs, "{}", Secret, isTls: true);

        public Task<RemoteResponse> TriggerShutdownAsync(Guid taskId, string nonce)
            => CallAuthedAsync("triggerShutdown", DeviceId, nonce, BaseMs,
                "{\"taskId\":\"" + taskId.ToString("D") + "\"}", Secret, isTls: true);

        public Task<RemoteResponse> CancelShutdownAsync(Guid taskId, string nonce)
            => CallAuthedAsync("cancelShutdown", DeviceId, nonce, BaseMs,
                "{\"taskId\":\"" + taskId.ToString("D") + "\"}", Secret, isTls: true);

        public Task<RemoteResponse> CallAuthedAsync(
            string method,
            string deviceId,
            string nonce,
            long timestamp,
            string paramsJson,
            byte[]? secret,
            bool isTls = true)
        {
            var raw = "{\"version\":1,\"deviceId\":\"" + deviceId
                + "\",\"timestamp\":" + timestamp
                + ",\"nonce\":\"" + nonce
                + "\",\"method\":\"" + method
                + "\",\"params\":" + paramsJson + "}";
            using var document = JsonDocument.Parse(raw);
            var payload = new RemotePayload
            {
                Version = 1,
                DeviceId = deviceId,
                Timestamp = timestamp,
                Nonce = nonce,
                Method = method,
                Params = document.RootElement.GetProperty("params").Clone()
            };
            var envelope = new RemoteRequestEnvelope
            {
                JsonRpc = "2.0",
                Id = 1,
                Payload = raw,
                Hmac = secret is null ? string.Empty : Hmac(secret, raw)
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
    }

    private static string Hmac(byte[] secret, string raw)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>内存存储：写时落文档 + 记录写路径（本地文档只读契约的观测点）。</summary>
    private sealed class RecordingStorage : IStorage
    {
        private readonly Dictionary<string, JsonElement> _documents = new();
        private readonly List<string> _writePaths = new();

        public IReadOnlyList<string> WritePaths => _writePaths;

        public void Seed(string relativePath, string json)
        {
            using var document = JsonDocument.Parse(json);
            _documents[relativePath] = document.RootElement.Clone();
        }

        public Task<StorageReadResult<T>> ReadAsync<T>(
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!_documents.TryGetValue(relativePath, out var element))
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.NotFound
                });
            }

            return Task.FromResult(new StorageReadResult<T>
            {
                Status = StorageReadStatus.Success,
                Value = element.Deserialize<T>()
            });
        }

        public Task<StorageWriteResult> WriteAsync<T>(
            string relativePath,
            T value,
            CancellationToken cancellationToken)
        {
            _writePaths.Add(relativePath);
            var json = JsonSerializer.Serialize(value);
            Seed(relativePath, json);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success,
                BackupPath = relativePath + ".bak"
            });
        }
    }

    private sealed class RecordingFakeEngine : ISchedulerEngine
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

        public List<RemoteTriggerTaskCommand> RemoteTriggers { get; } = new();

        public List<RemoteCancelTaskCommand> RemoteCancels { get; } = new();

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SchedulerCommandResult> SubmitAsync(
            SchedulerCommand command,
            CancellationToken cancellationToken)
        {
            Submitted.Add(command);
            if (command is RemoteTriggerTaskCommand trigger)
            {
                RemoteTriggers.Add(trigger);
            }

            if (command is RemoteCancelTaskCommand cancel)
            {
                RemoteCancels.Add(cancel);
            }

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
        private readonly TimeZoneInfo _timeZone;

        public FakeClock(DateTimeOffset utcNow, TimeZoneInfo timeZone)
        {
            UtcNow = utcNow;
            _timeZone = timeZone;
        }

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone => _timeZone;
    }

    private sealed class ControllableDeadline : IAsyncDeadline
    {
        private readonly List<TaskCompletionSource> _waiters = new();
        private readonly object _gate = new();

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        public Task WaitUntilAsync(DateTimeOffset utcDeadline, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiters.Add(tcs);
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => tcs.TrySetCanceled());
            }

            return tcs.Task;
        }

        public void CompleteNext()
        {
            TaskCompletionSource? next = null;
            lock (_gate)
            {
                while (_waiters.Count > 0)
                {
                    next = _waiters[0];
                    _waiters.RemoveAt(0);
                    if (!next.Task.IsCompleted)
                    {
                        break;
                    }

                    next = null;
                }
            }

            next?.TrySetResult();
        }
    }

    private sealed class CountingIdentifierGenerator : IIdentifierGenerator
    {
        private int _counter;

        public Guid NewId()
        {
            var value = System.Threading.Interlocked.Increment(ref _counter);
            return Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
        }
    }

    private sealed class EngineScope : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public EngineScope(SchedulerEngine engine)
        {
            RunTask = engine.RunAsync(_cts.Token);
        }

        public Task RunTask { get; }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                RunTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class CountingHandler : IScheduledTaskHandler
    {
        private readonly IScheduledTaskHandler _inner;

        public CountingHandler(IScheduledTaskHandler inner) => _inner = inner;

        public int CallCount { get; private set; }

        public async Task HandleDueAsync(TaskInstance instance, CancellationToken cancellationToken)
        {
            CallCount++;
            await _inner.HandleDueAsync(instance, cancellationToken);
        }
    }

    private sealed class FixedConfigurationService : IConfigurationService
    {
        private readonly AppConfig _config;

        public FixedConfigurationService(AppConfig config) => _config = config;

        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ConfigurationLoadResult
            {
                Status = ConfigurationLoadStatus.Success,
                Config = _config
            });

        public Task<ConfigurationSaveResult> SaveAsync(AppConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ConfigurationSaveResult> CreateSafeDefaultAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedUnattendedPolicyService : IUnattendedPolicyService
    {
        private readonly Func<PowerAction, UnattendedAuthorizationDecision> _evaluate;

        public FixedUnattendedPolicyService(Func<PowerAction, UnattendedAuthorizationDecision> evaluate)
            => _evaluate = evaluate;

        public Task<UnattendedAuthorizationDecision> EvaluateAsync(
            PowerAction action,
            CancellationToken cancellationToken) => Task.FromResult(_evaluate(action));

        public Task<UnattendedEnableResult> EnableAsync(
            UnattendedEnableRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UnattendedRevokeResult> RevokeAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPowerService : IPowerService
    {
        private readonly List<PowerRequest> _requests = new();
        private readonly PowerResult _result;

        public RecordingPowerService(PowerResult result) => _result = result;

        public IReadOnlyList<PowerRequest> Requests => _requests;

        public Task<PowerResult> ExecuteAsync(PowerRequest request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(_result);
        }
    }
}
