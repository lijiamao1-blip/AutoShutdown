using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoShutdown.App.Infrastructure.Logging;
using AutoShutdown.App.Infrastructure.Remote;
using AutoShutdown.App.Presentation;
using AutoShutdown.Core.Abstractions;
using AutoShutdown.Core.Remote;
using AutoShutdown.Core.Scheduling;
using AutoShutdown.Core.State;
using AutoShutdown.Core.Storage;
using AutoShutdown.Core.Tasks;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>
/// S23 CP5 应用层：设置页远程控制分区 + 托盘高危提示。
/// <list type="bullet">
/// <item>分区 VM：默认关闭（白名单默认仅只读）、损坏配置 fail-closed、保存并应用（重启监听）、
/// 字段校验失败不落盘不重启、启用但监听失败如实上报、禁用停止监听、PIN 轮换/锁定解锁/设备移除。</item>
/// <item>服务器通知（高危提示来源）：triggerShutdown/cancelShutdown 被接受并派发 → 高危通知；
/// 被拒绝（白名单关闭）→ 不通知；queryStatus → 低危连接通知。全程真实 TCP/TLS（本机回环）。</item>
/// </list>
/// 任何路径不触碰真实电源；通知负载绝不含 PIN/secret/HMAC/私钥。
/// </summary>
public sealed class S23_CP5_RemoteSectionTests
{
    private const string Pin = "654321";
    private const string ServerName = "TestPC";
    private static readonly DateTimeOffset Base = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();
    private static readonly TimeZoneInfo FixedUtc8 = TimeZoneInfo.CreateCustomTimeZone(
        "FixedUtc8",
        TimeSpan.FromHours(8),
        "FixedUtc8",
        "FixedUtc8");

    // ================= 分区视图模型 =================

    [Fact(Timeout = 3000)]
    public async Task Default_NoSettings_Closed_SafeWhiteList()
    {
        using var harness = CreateVmHarness();

        await harness.Vm.RefreshAsync(CancellationToken.None);

        Assert.False(harness.Vm.Enabled);
        Assert.Equal("127.0.0.1", harness.Vm.ListenAddress);
        Assert.Equal("48620", harness.Vm.ListenPortText);
        Assert.True(harness.Vm.RequireTls);
        Assert.True(harness.Vm.WhitelistQueryStatus);
        Assert.True(harness.Vm.WhitelistListTasks);
        Assert.False(harness.Vm.WhitelistTriggerShutdown);
        Assert.False(harness.Vm.WhitelistCancelShutdown);
        Assert.Equal("未监听", harness.Vm.ServerStatusText);
        Assert.False(harness.Vm.HasError);
        Assert.False(harness.Server.IsRunning);
    }

    [Fact(Timeout = 3000)]
    public async Task Refresh_FromSavedSettings_LoadsFields()
    {
        using var harness = CreateVmHarness();
        harness.Storage.Seed(RemoteSettingsStore.FileName, JsonSerializer.Serialize(new RemoteSettingsDocument
        {
            Enabled = true,
            ListenAddress = "0.0.0.0",
            ListenPort = 49000,
            RequireTls = false,
            RequireClientCertificate = true,
            WhiteList = new RemoteCommandWhiteList { TriggerShutdown = true, CancelShutdown = true }
        }));

        await harness.Vm.RefreshAsync(CancellationToken.None);

        Assert.True(harness.Vm.Enabled);
        Assert.Equal("0.0.0.0", harness.Vm.ListenAddress);
        Assert.Equal("49000", harness.Vm.ListenPortText);
        Assert.False(harness.Vm.RequireTls);
        Assert.True(harness.Vm.RequireClientCertificate);
        Assert.True(harness.Vm.WhitelistTriggerShutdown);
        Assert.True(harness.Vm.WhitelistCancelShutdown);
        Assert.False(harness.Vm.HasError);
    }

    [Fact(Timeout = 3000)]
    public async Task CorruptSettings_FailClosed_ClosedAndError()
    {
        using var harness = CreateVmHarness();
        // 非法 JSON：存储层返回 Corrupt → 分区必须保持关闭并上报，绝不静默启用。
        harness.Storage.Seed(RemoteSettingsStore.FileName, "{not-valid-json");

        await harness.Vm.RefreshAsync(CancellationToken.None);

        Assert.False(harness.Vm.Enabled);
        Assert.True(harness.Vm.HasError);
        Assert.Contains("远程控制配置不可用", harness.Vm.ErrorText);
        Assert.False(harness.Server.IsRunning);
    }

    [Fact(Timeout = 3000)]
    public async Task Save_EnableWithTrigger_PersistsAndRestarts()
    {
        using var harness = CreateVmHarness();

        harness.Vm.Enabled = true;
        harness.Vm.WhitelistTriggerShutdown = true;
        await harness.Vm.SaveCommand.ExecuteAsync();

        Assert.Equal(1, harness.Server.StopCalls);
        Assert.Equal(1, harness.Server.StartCalls);
        Assert.True(harness.Server.IsRunning);
        Assert.Contains("已启用并监听 127.0.0.1:48620", harness.Vm.StatusText);
        Assert.False(harness.Vm.HasError);

        var load = await harness.SettingsStore.LoadAsync(CancellationToken.None);
        Assert.Equal(RemoteSettingsLoadStatus.Success, load.Status);
        Assert.True(load.Document!.Enabled);
        Assert.Equal("127.0.0.1", load.Document.ListenAddress);
        Assert.Equal(48620, load.Document.ListenPort);
        Assert.True(load.Document.WhiteList.TriggerShutdown);
        Assert.Contains(RemoteSettingsStore.FileName, harness.Storage.WritePaths);
    }

    [Fact(Timeout = 3000)]
    public async Task Save_InvalidPort_NoPersist_NoRestart()
    {
        using var harness = CreateVmHarness();

        harness.Vm.Enabled = true;
        harness.Vm.ListenPortText = "99999";
        await harness.Vm.SaveCommand.ExecuteAsync();

        Assert.True(harness.Vm.HasError);
        Assert.Contains("端口", harness.Vm.ErrorText);
        Assert.DoesNotContain(RemoteSettingsStore.FileName, harness.Storage.WritePaths);
        Assert.Equal(0, harness.Server.StopCalls);
        Assert.Equal(0, harness.Server.StartCalls);
        Assert.False(harness.Server.IsRunning);
    }

    [Fact(Timeout = 3000)]
    public async Task Save_InvalidAddress_NoPersist_NoRestart()
    {
        using var harness = CreateVmHarness();

        harness.Vm.Enabled = true;
        harness.Vm.ListenAddress = "not-an-ip";
        await harness.Vm.SaveCommand.ExecuteAsync();

        Assert.True(harness.Vm.HasError);
        Assert.Contains("IP", harness.Vm.ErrorText);
        Assert.DoesNotContain(RemoteSettingsStore.FileName, harness.Storage.WritePaths);
        Assert.Equal(0, harness.Server.StartCalls);
    }

    [Fact(Timeout = 3000)]
    public async Task Save_EnableButStartFails_HonestError()
    {
        using var harness = CreateVmHarness();
        harness.Server.ShouldStartSucceed = false;

        harness.Vm.Enabled = true;
        await harness.Vm.SaveCommand.ExecuteAsync();

        // 设置已持久化，但监听未启动 —— 如实显示，绝不假装成功。
        Assert.Equal(1, harness.Server.StopCalls);
        Assert.Equal(1, harness.Server.StartCalls);
        Assert.False(harness.Server.IsRunning);
        Assert.Contains("监听未启动", harness.Vm.StatusText);
        var load = await harness.SettingsStore.LoadAsync(CancellationToken.None);
        Assert.True(load.Document!.Enabled);
    }

    [Fact(Timeout = 3000)]
    public async Task Save_Disable_StopsServer()
    {
        using var harness = CreateVmHarness();
        harness.Storage.Seed(RemoteSettingsStore.FileName, JsonSerializer.Serialize(new RemoteSettingsDocument
        {
            Enabled = true,
            ListenPort = 48620
        }));
        harness.Server.IsRunning = true;
        await harness.Vm.RefreshAsync(CancellationToken.None);
        Assert.True(harness.Vm.Enabled);

        harness.Vm.Enabled = false;
        await harness.Vm.SaveCommand.ExecuteAsync();

        Assert.Equal(1, harness.Server.StopCalls);
        Assert.Equal(0, harness.Server.StartCalls);
        Assert.False(harness.Server.IsRunning);
        Assert.Equal("未监听", harness.Vm.ServerStatusText);
        Assert.Contains("已关闭", harness.Vm.StatusText);

        var load = await harness.SettingsStore.LoadAsync(CancellationToken.None);
        Assert.False(load.Document!.Enabled);
    }

    [Fact]
    public void RotatePin_DisplaysPinAndExpiry()
    {
        using var harness = CreateVmHarness();

        harness.Vm.RotatePinCommand.Execute(null);

        Assert.Equal(Pin, harness.Vm.PinDisplay);
        Assert.Contains("有效至", harness.Vm.PinExpiryText);
    }

    [Fact(Timeout = 3000)]
    public async Task LockedState_Unlock_ClearsLock()
    {
        using var harness = CreateVmHarness();
        harness.Storage.Seed(RemotePairingLockStore.FileName, JsonSerializer.Serialize(new RemotePairingLockDocument
        {
            SchemaVersion = RemotePairingLockDocument.CurrentSchemaVersion,
            FailedAttempts = RemoteProtocol.PairingMaxFailedAttempts,
            LockedUntilUtc = Base.AddHours(1)
        }));

        await harness.Vm.RefreshAsync(CancellationToken.None);
        Assert.True(harness.Vm.IsLocked);
        Assert.Contains("已锁定", harness.Vm.LockStatusText);

        await harness.Vm.UnlockCommand.ExecuteAsync();
        Assert.False(harness.Vm.IsLocked);
        Assert.Contains("未锁定", harness.Vm.LockStatusText);
    }

    [Fact(Timeout = 3000)]
    public async Task Devices_ListAndRemove()
    {
        using var harness = CreateVmHarness();
        harness.Storage.Seed(RemoteDevicesStore.FileName, JsonSerializer.Serialize(new RemoteDevicesDocument
        {
            SchemaVersion = RemoteDevicesDocument.CurrentSchemaVersion,
            Devices =
            [
                new PairedDevice
                {
                    DeviceId = "dev-1",
                    DeviceName = "PC-B",
                    PairedAtUtc = Base,
                    ProtectedSecretBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("PROT:x"))
                }
            ]
        }));

        await harness.Vm.RefreshAsync(CancellationToken.None);
        Assert.Single(harness.Vm.Devices);
        Assert.Equal("dev-1", harness.Vm.Devices[0].DeviceId);
        Assert.Equal("PC-B", harness.Vm.Devices[0].DeviceName);
        Assert.Contains("已配对设备 1 台", harness.Vm.DeviceCountText);

        harness.Vm.RemoveDeviceCommand.Execute(harness.Vm.Devices[0]);
        await WaitUntilAsync(() => harness.Vm.Devices.Count == 0);
        Assert.Equal("暂无已配对设备", harness.Vm.DeviceCountText);
    }

    // ================= 服务器通知（高危提示） =================

    [Fact(Timeout = 3000)]
    public async Task Server_TriggerShutdownAccepted_RaisesHighRiskNotification()
    {
        var taskId = Guid.NewGuid();
        using var harness = CreateServerHarness();
        harness.SeedTask(taskId);
        harness.SeedEnabled(requireTls: true, trigger: true, cancel: true);
        harness.RotatePin();
        await harness.Server.StartAsync(CancellationToken.None);

        var (deviceId, secret) = await harness.PairOverTlsAsync();
        await using var conn = await ConnectTlsAsync("127.0.0.1", harness.Port, trustServer: true);
        await conn.SendLineAsync(BuildEnvelope(
            deviceId, BaseMs, "n-trigger-ok", RemoteProtocol.MethodTriggerShutdown,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", secret));
        var response = ParseResponse(await conn.ReadLineAsync());

        Assert.Null(response.Error);
        await WaitUntilAsync(() => harness.Notifications.Any(n => n.Kind == RemoteServerNotificationKind.TriggerShutdown));
        Assert.Single(harness.Notifications.Where(n => n.Kind == RemoteServerNotificationKind.TriggerShutdown));
        var notification = harness.Notifications.Single(n => n.Kind == RemoteServerNotificationKind.TriggerShutdown);
        Assert.False(string.IsNullOrEmpty(notification.SourceIp));
    }

    [Fact(Timeout = 3000)]
    public async Task Server_TriggerShutdownRejected_NoHighRiskNotification()
    {
        var taskId = Guid.NewGuid();
        using var harness = CreateServerHarness();
        harness.SeedTask(taskId);
        // 白名单默认（只读）：triggerShutdown 未启用 → Forbidden，绝不产生高危提示。
        harness.SeedEnabled(requireTls: true, trigger: false, cancel: false);
        harness.RotatePin();
        await harness.Server.StartAsync(CancellationToken.None);

        var (deviceId, secret) = await harness.PairOverTlsAsync();
        await using var conn = await ConnectTlsAsync("127.0.0.1", harness.Port, trustServer: true);
        await conn.SendLineAsync(BuildEnvelope(
            deviceId, BaseMs, "n-trigger-no", RemoteProtocol.MethodTriggerShutdown,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", secret));
        var response = ParseResponse(await conn.ReadLineAsync());

        Assert.NotNull(response.Error);
        Assert.Equal((int)RemoteErrorCode.Forbidden, response.Error!.Code);
        Assert.DoesNotContain(
            harness.Notifications,
            n => n.Kind is RemoteServerNotificationKind.TriggerShutdown or RemoteServerNotificationKind.CancelShutdown);
    }

    [Fact(Timeout = 3000)]
    public async Task Server_QueryStatus_RaisesConnectionNotification()
    {
        using var harness = CreateServerHarness();
        harness.SeedEnabled(requireTls: true, trigger: false, cancel: false);
        harness.RotatePin();
        await harness.Server.StartAsync(CancellationToken.None);

        var (deviceId, secret) = await harness.PairOverTlsAsync();
        await using var conn = await ConnectTlsAsync("127.0.0.1", harness.Port, trustServer: true);
        await conn.SendLineAsync(BuildEnvelope(
            deviceId, BaseMs, "n-query", RemoteProtocol.MethodQueryStatus, "{}", secret));
        var response = ParseResponse(await conn.ReadLineAsync());

        Assert.Null(response.Error);
        // 配对与查询各产生一次低危连接通知。
        await WaitUntilAsync(() => harness.Notifications.Count(n => n.Kind == RemoteServerNotificationKind.Connection) >= 2);
    }

    [Fact(Timeout = 3000)]
    public async Task Server_CancelShutdownAccepted_RaisesHighRiskNotification()
    {
        var taskId = Guid.NewGuid();
        using var harness = CreateServerHarness();
        harness.SeedTask(taskId);
        harness.SeedEnabled(requireTls: true, trigger: false, cancel: true);
        harness.RotatePin();
        await harness.Server.StartAsync(CancellationToken.None);

        var (deviceId, secret) = await harness.PairOverTlsAsync();
        await using var conn = await ConnectTlsAsync("127.0.0.1", harness.Port, trustServer: true);
        await conn.SendLineAsync(BuildEnvelope(
            deviceId, BaseMs, "n-cancel-ok", RemoteProtocol.MethodCancelShutdown,
            "{\"taskId\":\"" + taskId.ToString("D") + "\"}", secret));
        var response = ParseResponse(await conn.ReadLineAsync());

        Assert.Null(response.Error);
        await WaitUntilAsync(() => harness.Notifications.Any(n => n.Kind == RemoteServerNotificationKind.CancelShutdown));
        Assert.Single(harness.Notifications.Where(n => n.Kind == RemoteServerNotificationKind.CancelShutdown));
    }

    // ================= 分区 VM 夹具 =================

    private sealed class VmHarness : IDisposable
    {
        public RecordingStorage Storage { get; }
        public FakeClock Clock { get; }
        public FakeRemoteServer Server { get; }
        public RemoteSettingsStore SettingsStore { get; }
        public RemoteSectionViewModel Vm { get; }

        public VmHarness()
        {
            Storage = new RecordingStorage();
            Clock = new FakeClock(Base, FixedUtc8);
            Server = new FakeRemoteServer();
            SettingsStore = new RemoteSettingsStore(Storage);
            var devicesStore = new RemoteDevicesStore(Storage);
            var lockStore = new RemotePairingLockStore(Storage);
            var pairing = new PairingService(
                devicesStore,
                lockStore,
                new FakeSecretProtector(),
                Clock,
                new FakeAuditLog([]),
                pinGenerator: () => Pin,
                secretGenerator: () => Enumerable.Range(0, RemoteProtocol.SharedSecretBytes)
                    .Select(i => (byte)(i + 1)).ToArray());
            Vm = new RemoteSectionViewModel(
                SettingsStore,
                pairing,
                Server,
                Clock);
        }

        public void Dispose()
        {
        }
    }

    private static VmHarness CreateVmHarness() => new();

    private sealed class FakeRemoteServer : IRemoteServerControl
    {
        public bool ShouldStartSucceed { get; set; } = true;

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public bool IsRunning { get; set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            if (ShouldStartSucceed)
            {
                IsRunning = true;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalls++;
            IsRunning = false;
            return Task.CompletedTask;
        }
    }

    // ================= 服务器通知夹具（真实 TCP/TLS） =================

    private sealed class ServerHarness : IDisposable
    {
        public RecordingStorage Storage { get; }
        public FakeClock Clock { get; }
        public List<RemoteAuditEntry> Audit { get; } = new();
        public FakeLogger Logger { get; } = new();
        public RemoteSettingsStore SettingsStore { get; }
        public FakeSchedulerEngine Engine { get; } = new();
        public FakeTaskService Tasks { get; } = new();
        public PairingService Pairing { get; }
        public RemoteServer Server { get; }
        public List<RemoteServerNotification> Notifications { get; } = new();

        private readonly string _tempRoot;

        public int Port { get; }

        public ServerHarness()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "autoshutdown-s23-cp5-server", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            Storage = new RecordingStorage();
            Clock = new FakeClock(Base, TimeZoneInfo.Utc);
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
            var certificateService = new RemoteCertificateService(
                new FakeSecretProtector(),
                Clock,
                Path.Combine(_tempRoot, "remote-server-cert.dpapi"),
                Path.Combine(_tempRoot, "remote-imported-cert-password.dpapi"));
            var handler = new RemoteRequestHandler(
                Pairing,
                authenticator,
                Engine,
                Tasks,
                Clock,
                new FakeAuditLog(Audit),
                () => ServerName);
            Server = new RemoteServer(
                SettingsStore,
                certificateService,
                handler,
                Clock,
                new FakeAuditLog(Audit),
                Logger);
            Port = GetFreePort();
            Server.Notification += OnNotification;
        }

        public void SeedTask(Guid taskId)
        {
            Tasks.Seed(new TaskDefinition
            {
                Id = taskId,
                Kind = TaskKind.DailyAt,
                Action = PowerAction.Shutdown,
                TargetTimeOfDay = new TimeOnly(22, 0),
                IsEnabled = true,
                UseUnattended = false,
                CreatedAt = Base
            });
        }

        public void SeedSettings(RemoteSettingsDocument settings)
            => Storage.Seed(RemoteSettingsStore.FileName, JsonSerializer.Serialize(settings));

        public void SeedEnabled(bool requireTls, bool trigger, bool cancel)
            => SeedSettings(EnabledSettings(Port, requireTls, trigger, cancel));

        public void RotatePin() => Pairing.RotatePin();

        public async Task<(string DeviceId, byte[] Secret)> PairOverTlsAsync()
        {
            await using var pair = await ConnectTlsAsync("127.0.0.1", Port, trustServer: true);
            await pair.SendLineAsync(BuildEnvelope(
                string.Empty, BaseMs, "n-pair", RemoteProtocol.MethodPair,
                "{\"deviceName\":\"PC-B\",\"pin\":\"" + Pin + "\"}", secret: null));
            var pairResponse = ParseResponse(await pair.ReadLineAsync());
            Assert.Null(pairResponse.Error);
            var result = pairResponse.Result!.Value.Deserialize<RemotePairResult>()!;
            return (result.DeviceId, Convert.FromBase64String(result.SharedSecret));
        }

        private void OnNotification(object? sender, RemoteServerNotification notification)
        {
            lock (Notifications)
            {
                Notifications.Add(notification);
            }
        }

        public void Dispose()
        {
            Server.Notification -= OnNotification;
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

    private static ServerHarness CreateServerHarness() => new();

    private static RemoteSettingsDocument EnabledSettings(int port, bool requireTls, bool trigger, bool cancel) => new()
    {
        Enabled = true,
        ListenAddress = "127.0.0.1",
        ListenPort = port,
        RequireTls = requireTls,
        UseImportedCertificate = false,
        WhiteList = new RemoteCommandWhiteList
        {
            QueryStatus = true,
            ListTasks = true,
            TriggerShutdown = trigger,
            CancelShutdown = cancel
        }
    };

    // ================= 通用夹具 =================

    private sealed class RecordingStorage : IStorage
    {
        private readonly Dictionary<string, string> _documents = new();
        private readonly List<string> _writePaths = new();

        public IReadOnlyList<string> WritePaths => _writePaths;

        public void Seed(string relativePath, string json) => _documents[relativePath] = json;

        public Task<StorageReadResult<T>> ReadAsync<T>(string relativePath, CancellationToken cancellationToken)
        {
            if (!_documents.TryGetValue(relativePath, out var json))
            {
                return Task.FromResult(new StorageReadResult<T> { Status = StorageReadStatus.NotFound });
            }

            try
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Success,
                    Value = JsonSerializer.Deserialize<T>(json)
                });
            }
            catch (JsonException)
            {
                return Task.FromResult(new StorageReadResult<T>
                {
                    Status = StorageReadStatus.Corrupt,
                    Error = "Simulated corrupt document."
                });
            }
        }

        public Task<StorageWriteResult> WriteAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
        {
            _writePaths.Add(relativePath);
            _documents[relativePath] = JsonSerializer.Serialize(value);
            return Task.FromResult(new StorageWriteResult
            {
                Status = StorageWriteStatus.Success,
                BackupPath = relativePath + ".bak"
            });
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

        public void Seed(TaskDefinition definition) => _definitions[definition.Id] = definition;

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
        public FakeClock(DateTimeOffset utcNow, TimeZoneInfo timeZone)
        {
            UtcNow = utcNow;
            LocalTimeZone = timeZone;
        }

        public DateTimeOffset UtcNow { get; set; }

        public TimeZoneInfo LocalTimeZone { get; }
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

    // ================= 线上客户端 =================

    private static string BuildEnvelope(
        string deviceId,
        long timestamp,
        string nonce,
        string method,
        string paramsJson,
        byte[]? secret)
    {
        var rawPayload = "{\"version\":1,\"deviceId\":\"" + deviceId
            + "\",\"timestamp\":" + timestamp
            + ",\"nonce\":\"" + nonce
            + "\",\"method\":\"" + method
            + "\",\"params\":" + paramsJson + "}";
        var hmac = secret is null ? string.Empty : Hmac(secret, rawPayload);
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1L, payload = rawPayload, hmac });
    }

    private static string Hmac(byte[] secret, string raw)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static RemoteResponse ParseResponse(string line)
        => JsonSerializer.Deserialize<RemoteResponse>(line)!;

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<ClientConnection> ConnectTlsAsync(string host, int port, bool trustServer)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port);
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => trustServer
            });
            return new ClientConnection(client, ssl);
        }
        catch
        {
            client.Dispose();
            throw;
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

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;

        public ClientConnection(TcpClient client, Stream stream)
        {
            _client = client;
            _stream = stream;
        }

        public async Task SendLineAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
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
